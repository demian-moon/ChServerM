using FbsClassM;
using Google.FlatBuffers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EcsServerLibM
{

	public interface IHasGameOid
	{
		public long Oid { get; }
	}


	public abstract class PkObjM : IHasGameOid
	{
		private uint _pid;                      // packet id
		private TcpClient _tc;                  // tcpClient
		private CancellationTokenSource _cts;  // 취소 토큰 소스

		public CompressAndEncryptM _compEnc;

		public uint Pid { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _pid; set => _pid = value; }           // packet id
		public TcpClient Tc { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _tc; set => _tc = value; }         // TcpClient
		public CancellationTokenSource Cts { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _cts; set => _cts = value; }    // 타이머등을 통한 강제 종료에 쓰이는 캔슬 토큰소스        


		private long _lastPkRecvTick;   // 마지막 유저의 패킷 도착 시간
		public long LastPkRecvTick { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _lastPkRecvTick; set => _lastPkRecvTick = value; }

		// 버퍼 전송 관련        
		protected byte[] _sendBuffer;
		protected int _sendBufferLength;
		private int MaxBufferSize = 65536;  //8192 * 8; // Maximum buffer size in bytes
		private const int BatchSize = 16384; // Adjust this value based on performance testing


		protected PkObjM()
		{
			_sendBuffer = new byte[MaxBufferSize];
			_sendBufferLength = 0;
		}


		public void MakeOid()
		{
			_oid = GlobalM.MakeGameOid();
		}

		public void WritePkTimeNow() // 현재 시간 찍기
		{
			LastPkRecvTick = TickTimeM.GTick;
		}

		protected long _oid;
		public long Oid { get => _oid; set => _oid = value; }           // Object Id


		/// <summary>
		/// Serialize 한 후에 패킷을 보내는 함수 
		/// </summary>
		/// <param name="ePacketType"></param>
		/// <param name="sendData"></param>
		/// <returns></returns>
		abstract public void SerializeSendPacket(PACKET_TYPE ePacketType, byte[] sendData);

		abstract public void FlushSendBuffer();


		public void WriteSendBuffer(PACKET_TYPE ePacketType, byte[] sendData)
		{
			if (Tc.Connected == false)
				return;

			var fbb = new FlatBufferBuilder(64);
			var lengthSendData = 0;

			// 헤더 만들기
			var osPkHead = FbsPkHeadM.CreateFbsPkHeadM(fbb, Pid, PacketM.gConHeadLen, 1); // 첵섬 1
			fbb.Finish(osPkHead.Value);

			var byteHeader = fbb.SizedByteArray();
			var lengthHeader = byteHeader.Length;



			// 콘텐츠 헤더 만들기
			fbb = new FlatBufferBuilder(64);
			if (sendData != null)    // 데이터가 있으면 보낸다           
				lengthSendData = sendData.Length;

			var osConHead = FbsContentHeadM.CreateFbsContentHeadM(fbb, (ushort)ePacketType, lengthSendData);
			fbb.Finish(osConHead.Value);

			var byteConHeader = fbb.SizedByteArray();
			var lengthConHeader = byteConHeader.Length;

			// 전송 데이터 만들기            
			byte[] combinePk;
			int lengthCombinePk = lengthHeader + lengthConHeader;


			lengthCombinePk += lengthSendData;
			combinePk = ArrayPool<byte>.Shared.Rent(lengthCombinePk);

			Buffer.BlockCopy(byteHeader, 0, combinePk, 0, lengthHeader);
			Buffer.BlockCopy(byteConHeader, 0, combinePk, lengthHeader, lengthConHeader);
			if (sendData != null)
				Buffer.BlockCopy(sendData, 0, combinePk, lengthHeader + lengthConHeader, lengthSendData);

			byte[] finalData = combinePk;
			int finalDataLen = lengthCombinePk;

			if (_compEnc != null) // 인크립터 설정되어 있으면 압축, 암호화 해서 보냄
			{
				bool isCompress = _compEnc.Compress(combinePk, lengthCombinePk, out byte[] byteComp); // 압축 먼저

				var byteEncrypt = _compEnc.Encrypt(byteComp);   // 클라는 Aes, 서버는 Xor 암호화                
				ArrayPool<byte>.Shared.Return(combinePk);    // 리소스 반환 // 순서 중요 (Compress 안됐을 수 있으니 Encrypt 후에 불러야 됨)

				int lengthByteEcrypt = byteEncrypt.Length;          // Encrypt 한 후 사이즈

				if (isCompress == true)
				{
					_compEnc.ReturnPoolAfterCompress(byteComp); // arrayPool 리소스 리턴     
				}

				// 압축 암호화 헤더 만들기
				fbb = new FlatBufferBuilder(64);
				var osEncryptHeader = FbsEncryptHeadM.CreateFbsEncryptHeadM(fbb, (sbyte)(isCompress ? 1 : 0), lengthByteEcrypt, lengthCombinePk);
				fbb.Finish(osEncryptHeader.Value);

				var byteEncryptHeader = fbb.SizedByteArray(); // 압축 헤더

				int lengthEncryptHeader = byteEncryptHeader.Length;

				int lengthEncryptPk = lengthEncryptHeader + lengthByteEcrypt;
				byte[] encryptPk = ArrayPool<byte>.Shared.Rent(lengthEncryptPk); // 어레이 풀 렌트

				Buffer.BlockCopy(byteEncryptHeader, 0, encryptPk, 0, lengthEncryptHeader);
				Buffer.BlockCopy(byteEncrypt, 0, encryptPk, lengthEncryptHeader, lengthByteEcrypt);

				finalData = encryptPk;
				finalDataLen = lengthEncryptPk;
			}

			int lengthCheck = finalDataLen + _sendBufferLength;   //현재 패킷 길이 + 현재 버퍼길이
			if (lengthCheck > BatchSize)
			{
				FlushSendBuffer();
				if(finalDataLen > MaxBufferSize) // 버퍼 비웠는데도 보낼 사이즈가 sendBuffer보다 크면
				{
					MaxBufferSize = finalDataLen; // 버퍼 사이즈를 늘린다
					_sendBuffer = new byte[MaxBufferSize]; // 버퍼 사이즈가 너무 크면 초기화
				}
			}

			Buffer.BlockCopy(finalData, 0, _sendBuffer, _sendBufferLength, finalDataLen);
			_sendBufferLength += finalDataLen;

		}

		// 가능?
		public void WriteSendBuffer(PACKET_TYPE ePacketType, ReadOnlySequence<byte> sendData)
		{
			if (Tc.Connected == false)
				return;

			var fbb = new FlatBufferBuilder(64);
			var lengthSendData = 0;

			// 헤더 만들기
			var osPkHead = FbsPkHeadM.CreateFbsPkHeadM(fbb, Pid, PacketM.gConHeadLen, 1); // 첵섬 1
			fbb.Finish(osPkHead.Value);

			var byteHeader = fbb.SizedByteArray();
			var lengthHeader = byteHeader.Length;



			// 콘텐츠 헤더 만들기
			fbb = new FlatBufferBuilder(64);
			lengthSendData = (int)sendData.Length;

			var osConHead = FbsContentHeadM.CreateFbsContentHeadM(fbb, (ushort)ePacketType, lengthSendData);
			fbb.Finish(osConHead.Value);

			var byteConHeader = fbb.SizedByteArray();
			var lengthConHeader = byteConHeader.Length;

			// 전송 데이터 만들기            
			byte[] combinePk;
			int lengthCombinePk = lengthHeader + lengthConHeader;


			lengthCombinePk += lengthSendData;
			combinePk = ArrayPool<byte>.Shared.Rent(lengthCombinePk);

			Buffer.BlockCopy(byteHeader, 0, combinePk, 0, lengthHeader);
			Buffer.BlockCopy(byteConHeader, 0, combinePk, lengthHeader, lengthConHeader);
			if (lengthSendData != 0)
			{
				//Buffer.BlockCopy(sendData, 0, combinePk, lengthHeader + lengthConHeader, lengthSendData);                            
				Span<byte> spanCombinePk = combinePk.AsSpan();
				sendData.CopyTo(spanCombinePk.Slice(lengthHeader + lengthConHeader));
			}

			byte[] finalData = combinePk;
			int finalDataLen = lengthCombinePk;

			if (_compEnc != null) // 인크립터 설정되어 있으면 압축, 암호화 해서 보냄
			{
				bool isCompress = _compEnc.Compress(combinePk, lengthCombinePk,out byte[] byteComp); // 압축 먼저

				var byteEncrypt = _compEnc.Encrypt(byteComp);   // 클라는 Aes, 서버는 Xor 암호화                
				ArrayPool<byte>.Shared.Return(combinePk);    // 리소스 반환 // 순서 중요 (Compress 안됐을 수 있으니 Encrypt 후에 불러야 됨)

				int lengthByteEcrypt = byteEncrypt.Length;          // Encrypt 한 후 사이즈

				if (isCompress == true)
				{
					_compEnc.ReturnPoolAfterCompress(byteComp); // arrayPool 리소스 리턴     
				}

				// 압축 암호화 헤더 만들기
				fbb = new FlatBufferBuilder(64);
				var osEncryptHeader = FbsEncryptHeadM.CreateFbsEncryptHeadM(fbb, (sbyte)(isCompress ? 1 : 0), lengthByteEcrypt, lengthCombinePk);
				fbb.Finish(osEncryptHeader.Value);

				var byteEncryptHeader = fbb.SizedByteArray(); // 압축 헤더

				int lengthEncryptHeader = byteEncryptHeader.Length;

				int lengthEncryptPk = lengthEncryptHeader + lengthByteEcrypt;
				byte[] encryptPk = ArrayPool<byte>.Shared.Rent(lengthEncryptPk); // 어레이 풀 렌트

				Buffer.BlockCopy(byteEncryptHeader, 0, encryptPk, 0, lengthEncryptHeader);
				Buffer.BlockCopy(byteEncrypt, 0, encryptPk, lengthEncryptHeader, lengthByteEcrypt);

				finalData = encryptPk;
				finalDataLen = lengthEncryptPk;
			}

			int lengthCheck = finalDataLen + _sendBufferLength;   //현재 패킷 길이 + 현재 버퍼길이
			if (lengthCheck > BatchSize)
			{
				FlushSendBuffer();
				if (finalDataLen > MaxBufferSize) // 버퍼 비웠는데도 보낼 사이즈가 sendBuffer보다 크면
				{
					MaxBufferSize = finalDataLen; // 버퍼 사이즈를 늘린다
					_sendBuffer = new byte[MaxBufferSize]; // 버퍼 사이즈가 너무 크면 초기화
				}
			}

			Buffer.BlockCopy(finalData, 0, _sendBuffer, _sendBufferLength, finalDataLen);
			_sendBufferLength += finalDataLen;

		}

		//public void WriteSendBuffer(PACKET_TYPE ePacketType, byte[] sendData)
		//{
		//    if (this.Tc.Connected == false)
		//        return;


		//    int lengthCheck = PacketM.gPkHeadLen + PacketM.gConHeadLen + sendData.Length + _sendBufferLength;   //현재 패킷 길이 + 현재 버퍼길이
		//    if (lengthCheck > BatchSize)
		//    {
		//        FlushBuffer();
		//    }

		//    var fbb = new FlatBufferBuilder(1);
		//    var lengthSendData = 0;
		//    var lengthHeader = PacketM.gPkHeadLen;
		//    var lengthConHeader = PacketM.gConHeadLen;

		//    // 헤더 만들기
		//    var osPkHead = FbsPkHeadM.CreateFbsPkHeadM(fbb, Pid, PacketM.gConHeadLen, 1); // 첵섬 1
		//    fbb.Finish(osPkHead.Value);
		//    var byteHeader = fbb.SizedByteArray();

		//    // 콘텐츠 헤더 만들기
		//    fbb = new FlatBufferBuilder(1);
		//    int sendDataLen = 0;
		//    if (sendData != null)
		//        sendDataLen = sendData.Length;
		//    var osConHead = FbsContentHeadM.CreateFbsContentHeadM(fbb, (ushort)ePacketType, sendDataLen);
		//    fbb.Finish(osConHead.Value);
		//    var byteConHeader = fbb.SizedByteArray();

		//    // 전송 데이터 만들기                
		//    int lengthCombine = lengthHeader + lengthConHeader;
		//    if (sendData != null)    // 데이터가 있으면 보낸다           
		//        lengthSendData = sendData.Length;


		//    lock(_lock)
		//    {  
		//        Buffer.BlockCopy(byteHeader, 0, _sendBuffer, _sendBufferLength, lengthHeader);
		//        Buffer.BlockCopy(byteConHeader, 0, _sendBuffer, _sendBufferLength + lengthHeader, lengthConHeader);
		//        if (sendData != null)
		//            Buffer.BlockCopy(sendData, 0, _sendBuffer, _sendBufferLength + lengthHeader + lengthConHeader, lengthSendData);

		//        _sendBufferLength += lengthHeader + lengthConHeader + lengthSendData;                
		//    }            

		//    if (_sendBufferLength >= BatchSize)
		//    {
		//        FlushBuffer();
		//    }
		//}        

	}


	//public class ObservablePkObjM : PkObjM, IObservable<ObjM>
	//{
	//    protected ConcurrentDictionary<long, IObserver<ObjM>> _dicObservers = new ConcurrentDictionary<long, IObserver<ObjM>>();

	//    public IDisposable Subscribe(IObserver<ObjM> observer)
	//    {
	//        long oid = (observer as ObjM).Oid;
	//        _dicObservers.TryAdd(oid, observer);

	//        return new ConcurrentUnsubscriberM<ObjM>(oid, _dicObservers);
	//    }
	//}

}
