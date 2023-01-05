﻿using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace JEngine.Core
{
    public class JStream : Stream
    {
        private byte[] _buffer; // Either allocated internally or externally.
        private readonly int _origin; // For user-provided arrays, start at this origin
        private int _position; // read/write head.
        private int _length; // Number of bytes within the memory stream
        private int _capacity; // length of usable portion of buffer for stream
        private byte[] _key; //解密密码
        private string _defaultKey = "hello_JEngine_!_";

        private bool _encrypted = true; //是否aes加密了
        private readonly bool _exposable; // Whether the array can be returned to the user.
        private bool _isOpen; // Is this stream open or closed?

        private const int MemStreamMaxLength = Int32.MaxValue;

        public bool Encrypted
        {
            get => _encrypted;
            set => _encrypted = value;
        }

        public JStream(byte[] buffer, string key)
        {
            _buffer = new byte[buffer.Length];
            buffer.AsSpan().CopyTo(_buffer);
            _length = _capacity = buffer.Length;

            _exposable = false;
            _origin = 0;
            _isOpen = true;
            if (key.Length < 16)
            {
                key = InitJEngine.Instance.key.Length < 16 ? _defaultKey : InitJEngine.Instance.key;
            }

            _key = Encoding.UTF8.GetBytes(key);
        }

        public override bool CanRead => _isOpen;

        public override bool CanSeek => _isOpen;

        public override bool CanWrite => false;

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    _isOpen = false;
                    _buffer = null;
                }
            }
            finally
            {
                // Call base.Close() to cleanup async IO resources
                base.Dispose(disposing);
            }
        }

        public override void Flush()
        {
        }


        public virtual byte[] GetBuffer()
        {
            if (!_exposable)
                throw new UnauthorizedAccessException("UnauthorizedAccess to get member buffer");
            if (_buffer == null) return Array.Empty<byte>();
            return _buffer;
        }

        public virtual bool TryGetBuffer(out ArraySegment<byte> buffer)
        {
            if (!_exposable)
            {
                buffer = default(ArraySegment<byte>);
                return false;
            }

            if (_buffer == null)
            {
                buffer = new ArraySegment<byte>(Array.Empty<byte>());
                return false;
            }

            buffer = new ArraySegment<byte>(_buffer, _origin, (_length - _origin));
            return true;
        }

        // Gets & sets the capacity (number of bytes allocated) for this stream.
        // The capacity cannot be set to a value less than the current length
        // of the stream.
        // 
        public virtual int Capacity
        {
            get
            {
                if (!_isOpen) Log.PrintError("stream is closed");
                return _capacity - _origin;
            }
        }

        public override long Length
        {
            get
            {
                if (!_isOpen) Log.PrintError("stream is closed");
                return _length - _origin;
            }
        }

        public override long Position
        {
            get
            {
                if (!_isOpen) Log.PrintError("stream is closed");
                return _position - _origin;
            }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "value < 0 is invalid");
                if (!_isOpen) Log.PrintError("stream is closed");

                if (value > MemStreamMaxLength)
                    throw new ArgumentOutOfRangeException(nameof(value), "value > stream length is invalid");
                _position = _origin + (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer), "buffer == null");
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset < 0");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count < 0");
            if (buffer.Length - offset < count)
                throw new ArgumentException("invalid buffer length");

            if (!_isOpen || _buffer == null) throw new EndOfStreamException("stream is closed");

            int n = _length - _position;
            if (n > count) n = count;
            if (n <= 0)
                return 0;

            /*
             * JEngine的分块解密
             * 理论上，aes是 每16字节为单位 加密
             * 所以只需给buffer以16位单位切割即可
             */
            if (_encrypted)
            {
                try
                {
                    var decrypted = GetBytesAt(_position, count);
                    decrypted.AsSpan(0, n).CopyTo(buffer.AsSpan(offset)); //复制过去
                    //返还借的数组
                    ArrayPool<byte>.Shared.Return(decrypted);
                }
                catch (Exception ex)
                {
                    Log.PrintError(ex);
                    throw;
                }
            }
            else
            {
                //没加密的直接读就好
                _buffer.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset));
            }

            _position += n;

            return n;
        }

        /// <summary>
        /// 获取特定位置的真实数据（包含解密过程）
        /// </summary>
        /// <param name="start"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private byte[] GetBytesAt(int start, int length)
        {
            int remainder = start % 16; // 余数
            int offset = start - remainder; // 偏移值，截取开始的地方，比如 67 变 64
            int count = length +
                        (32 - length %
                            16); //获得需要切割的数组的长度，比如 77 变 96(80+16)，77+ （32- 77/16的余数） = 77 + （32-13） = 77 + 19 = 96，多16位确保不丢东西
            var result = ArrayPool<byte>.Shared.Rent(length); //返回值，长度为length

            //现在需要将buffer切割，从offset开始，到count为止
            var encryptedData = ArrayPool<byte>.Shared.Rent(count); //创建加密数据数组
            Array.Clear(encryptedData, 0, count);
            var l = _length - offset;
            if (count > l) count = l;
            _buffer.AsSpan(offset, count).CopyTo(encryptedData); //从原始数据里分割出来

            //给encryptedData解密
            var decrypt = CryptoMgr.AesDecrypt(encryptedData, _key, 0, count, CipherMode.ECB, PaddingMode.None);
            //截取decrypt，从remainder开始，到length为止，比如余数是3，那么从3-1的元素开始
            offset = remainder;

            //返还借的数组
            ArrayPool<byte>.Shared.Return(encryptedData);

            //这里有个问题，比如decrypt有16字节，而result是12字节，offset是8，那么12+8 > 16，就会出现错误
            //所以这里要改一下
            var total = offset + length;
            var dLength = decrypt.Length;
            if (total > dLength)
            {
                length = decrypt.Length;
            }

            decrypt.AsSpan(offset, length).CopyTo(result.AsSpan());
            return result;
        }

        public override long Seek(long offset, SeekOrigin loc)
        {
            if (!_isOpen) Log.PrintError("stream is closed");

            if (offset > MemStreamMaxLength)
                throw new ArgumentOutOfRangeException(nameof(offset), "offset > stream length is invalid");
            switch (loc)
            {
                case SeekOrigin.Begin:
                {
                    int tempPosition = unchecked(_origin + (int)offset);
                    if (offset < 0 || tempPosition < _origin)
                        throw new IOException("offset < 0 from the beginning of stream is invalid");
                    _position = tempPosition;
                    break;
                }
                case SeekOrigin.Current:
                {
                    int tempPosition = unchecked(_position + (int)offset);
                    if (unchecked(_position + offset) < _origin || tempPosition < _origin)
                        throw new IOException("offset is before the stream which is invalid");
                    _position = tempPosition;
                    break;
                }
                case SeekOrigin.End:
                {
                    int tempPosition = unchecked(_length + (int)offset);
                    if (unchecked(_length + offset) < _origin || tempPosition < _origin)
                        throw new IOException("offset is before the stream which is invalid");
                    _position = tempPosition;
                    break;
                }
                default:
                    throw new ArgumentException("invalid seek origin");
            }

            return _position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("JStream does not support write method!");
        }
    }
}