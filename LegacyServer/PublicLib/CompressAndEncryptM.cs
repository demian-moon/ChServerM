using K4os.Compression.LZ4;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace EcsServerLibM
{
	/// <summary>
	/// 압축(LZ4)과 Encrypt(Aes, Xor) 암호화를 하는 클래스
	/// </summary>
	public class CompressAndEncryptM : IDisposable
	{
		[Flags]
		public enum ENCRYPT_TYPE { NONE = 0, XOR = 1, AES = 2 }

		private ENCRYPT_TYPE encType;
		private ENCRYPT_TYPE decType;

		private Aes aes;
		private bool disposed = false;
		private byte[] xorKey;

		/// <summary>
		/// RSA 개인키
		/// </summary>
		public string RSAPrivateKeyMadeByClient { get; set; }
		public string RSAPrivateKeyMadeByServer { get; set; }

		public string RSAPublicKeyMadeByServer { get; set; }
		public string RSAPublicKeyMadeByClient { get; set; }

		public byte[] XorKey => xorKey;
		public byte[] AesKey => aes?.Key;
		public byte[] AesIV => aes?.IV;

		/// <summary>
		/// 압축(LZ4)과 Encrypt(Aes, Xor) 암호화를 하는 클래스 생성자
		/// </summary>
		/// <param name="encType">암호화 사용 로직</param>
		/// <param name="decType">복호화 사용 로직</param>
		public CompressAndEncryptM(ENCRYPT_TYPE encType, ENCRYPT_TYPE decType)
		{
			CreateEncDecType(encType, decType);
		}
		
		/// <summary>
		/// 서버에서 사용 생성자
		/// </summary>
		/// <param name="privateKeyMadeByServer">클라이언트에서 전달될 암호화 관련 정보를 풀 때 사용할 개인키</param>
		/// <param name="publicKeyMadeByClient">클라에서 전달된 공개키, 서버에서 전달할 암호화 정보(xor 키)를 암호화 할 때 쓰는 공개키 </param>
		public CompressAndEncryptM(string privateKeyMadeByServer, string publicKeyMadeByClient, string privateKeyMadeByClient, string publicKeyMadeByServer)
		{
			RSAPrivateKeyMadeByServer = privateKeyMadeByServer;
			RSAPublicKeyMadeByClient = publicKeyMadeByClient;

			RSAPrivateKeyMadeByClient = privateKeyMadeByClient;
			RSAPublicKeyMadeByServer = publicKeyMadeByServer;
		}

		public bool IsReady()
		{
			if(encType != ENCRYPT_TYPE.NONE && decType != ENCRYPT_TYPE.NONE)
			{
				return true;
			}

			return false;
		}


		public void CreateEncDecType(ENCRYPT_TYPE encType, ENCRYPT_TYPE decType)
		{
			this.encType = encType; // 디폴트 EncType을 결정
			this.decType = decType; // 디폴트 DecType을 결정

			if (encType == ENCRYPT_TYPE.AES) // 자동 생성된 AES 키와 IV를 사용
			{
				aes = Aes.Create();
				aes.Padding = PaddingMode.PKCS7;
				aes.KeySize = 128;
				aes.GenerateKey();
				aes.GenerateIV();
			}

			if (encType == ENCRYPT_TYPE.XOR) // 임의로 생성된 XOR 키를 사용 
			{
				xorKey = new byte[32];
				using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
				{
					rng.GetBytes(xorKey);
				}
			}
		}


		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] Encrypt(byte[] data)
		{
			byte[] rtnData = null;
			if (encType == ENCRYPT_TYPE.AES)
			{
				rtnData = AesEncrypt(data);
			}
			else if (encType == ENCRYPT_TYPE.XOR)
			{
				rtnData = XorEncrypt(data);
			}
			return rtnData;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] Decrypt(byte[] data, int dataLen)
		{
			byte[] rtnData = null;
			if (decType == ENCRYPT_TYPE.AES)
			{
				rtnData = AesDecrypt(data, dataLen);
			}
			else if (decType == ENCRYPT_TYPE.XOR)
			{
				rtnData = XorDecrypt(data);
			}
			return rtnData;
		}


		public void SetXorKey(byte[] xorKey)
		{
			this.xorKey = xorKey;
		}

		// AES 키와 IV를 전달받는 생성자
		public void SetAesKey(byte[] aesKey, byte[] aesIV)
		{
			if (aes == null)
				aes = Aes.Create();
			aes.Key = aesKey;
			aes.IV = aesIV;
		}

		// XOR 암호화
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		byte[] XorEncrypt(byte[] data)
		{
			if (xorKey == null)
			{
				throw new InvalidOperationException("XorEncrypt 불가");
			}

			var result = new byte[data.Length];
			for (int i = 0; i < data.Length; i++)
			{
				result[i] = (byte)(data[i] ^ XorKey[i % XorKey.Length]);
			}
			return result;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		byte[] XorDecrypt(byte[] encryptedData)
		{
			if (xorKey == null)
			{
				throw new InvalidOperationException("XorDecrypt 불가");
			}
			return XorEncrypt(encryptedData);
		}

		// AES 암호화
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		byte[] AesEncrypt(byte[] data)
		{
			if (aes == null)
			{
				throw new InvalidOperationException("AesEncrypt 불가");
			}

			using (var encryptor = aes.CreateEncryptor())
			{
				return encryptor.TransformFinalBlock(data, 0, data.Length);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		byte[] AesDecrypt(byte[] encryptedData, int encryptedDataLen) 
		{
			if (aes == null)
			{
				throw new InvalidOperationException("AesDecrypt 불가");
			}

			using (var decryptor = aes.CreateDecryptor())
			{
				return decryptor.TransformFinalBlock(encryptedData, 0, encryptedDataLen); // 실제 사용 버퍼사이즈 사용해야 한다
			}
		}

		// 
		/// <summary>
		/// LZ4 압축 메소드  
		/// </summary>
		/// <param name="originData"></param>
		/// <param name="iCompLen">-1 값이면 원본 사용이므로 arrayPool return 하면 안됨</param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool Compress(byte[] originData, int originDataLen, out byte[] compByte)
		{
			bool isCompress = false;						

			// 예상되는 최대 압축 크기 계산
			int maxLength = LZ4Codec.MaximumOutputSize(originDataLen);

			// 만약 예상 최대 크기가 원본 크기보다 크다면 압축하지 않고 원본 반환
			if (maxLength >= originDataLen)
			{
				// 압축할 필요가 없으므로 원본 데이터 유지
				isCompress = false;
				compByte = originData;    // 원본 그대로
				return isCompress;

			}

			// 압축할 데이터는 원본 데이터의 복사본
			//byte[] compressed = new byte[maxLength];
			compByte = ArrayPool<byte>.Shared.Rent(maxLength);
			//int compressedLength = LZ4Codec.Encode(data, 0, originalLength, compressed, 0, maxLength);
			LZ4Codec.Encode(originData, 0, originDataLen, compByte, 0, maxLength);
			isCompress = true;

			return isCompress;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ReturnPoolAfterCompress(byte[] rtnArray)
		{
			ArrayPool<byte>.Shared.Return(rtnArray);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] Decompress(byte[] compressedData, int originalLength)
		{
			var decompressed = new byte[originalLength];
			LZ4Codec.Decode(compressedData, decompressed);
			return decompressed;
		}



		// 자원 해제
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!disposed)
			{
				if (disposing)
				{
					aes?.Dispose();
				}
				disposed = true;
			}
		}

		~CompressAndEncryptM()
		{
			Dispose(false);
		}
	}
}
