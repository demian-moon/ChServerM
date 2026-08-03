using Collections.Pooled;
using FbsClassM;
using Google.FlatBuffers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{


	/// <summary>
	/// 파이프 리더로 부터 읽은 데이터를 가지고 헤더와 데이터를 차례로 만들고 실제 메모리 PacketData를 만들어 내는 클래스 
	/// </summary>
	//public abstract class AbMemPkFactory
	//{

	//	private CancellationTokenSource _cts;
	//	private TcpClient _tc;
		

	//	protected CancellationTokenSource Cts { get => _cts; set => _cts = value; }
	//	protected TcpClient Tc { get => _tc; set => _tc = value; }


	//	// MemPk를 어디로 보낼지 함수 구현 (서버, 클라가 다름)
	//	abstract public ValueTask SendMemPk(MemPacketM memPk);
	//	abstract public ValueTask SendEncMemPk(EncMemPacketM encMemPk);


	//	public AbMemPkFactory(TcpClient tc, CancellationTokenSource cts)
	//	{
	//		Tc = tc;
	//		Cts = cts;
			                  
	//	}

		
	//}



	/// <summary>
	/// 패킷 데이터를 가지고 실제 메모리상에 Packet데이터 외에 필요한 정보를 더해서 올려놓은 패킷
	/// </summary>
	public struct MemPacketM : IUIThreadCheck
	{

		private UserM _u;   // 유저
							//private PacketM _pk; // 패킷


		private TcpClient _tc;
		private FbsPkHeadM _pkHead;
		private FbsContentHeadM _conHead;
		private byte[] _conData;    // 다른곳에 넘겨서 쓰면 안된다. MemPkDispatcher.MemPkAction() 이후 바로 어레이 풀에 리턴함				

		/// <summary>
		/// ArrayPool 해제를 위해서 
		/// </summary>
		byte[] pooledPkHead;
		private byte[] pooledConHead;

		public TcpClient Tc { get => _tc; set => _tc = value; }

		public UserM U { get => _u; set => _u = value; }

		
		public FbsPkHeadM PkHead { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _pkHead; set => _pkHead = value; }
		public FbsContentHeadM ConHead { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _conHead; set => _conHead = value; }

		// ConData 다른곳에 넘겨서 저장해서 쓰면 안된다. MemPkDispatcher.MemPkAction() 이후 바로 어레이 풀에 리턴함
		public byte[] ConData { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _conData; set => _conData = value; }
		// 
		/// <summary>
		/// ConData가 ArrayPool에서 빌린 것이므로 ConData의 실제 길이는 반드시 아래 것을 사용한다. 
		/// </summary>
		public int ConDataLen { get; set; }

		/// <summary>
		/// FbsPacketM 을 가지고 만듬
		/// </summary>
		/// <param name="tc"></param>
		/// <param name="bytePacket"></param>
		//public MemPacketM(TcpClient tc, FbsPkHeadM pkHead, FbsContentHeadM conHead, byte[] conData)
		//{
		//	Tc = tc;
		//	PkHead = pkHead;
		//	ConHead = conHead;
		//	ConData = conData;
		//}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="tc"></param>
		/// <param name="pkHead"></param>
		/// <param name="pooledPkHead">ArrayPool 해제를 위한 byte []</param>
		/// <param name="conHead"></param>
		/// <param name="pooledConHead">ArrayPool 해제를 위한 byte[] </param>
		/// <param name="conData"></param>
		/// <param name="conDataLen">배열의 길이가 아니라 실제 사용하는 conData의 길이를 넘겨야 한다.</param>
		public MemPacketM(TcpClient tc, FbsPkHeadM pkHead, FbsContentHeadM conHead, byte[] conData) 
		{
			Tc = tc;
			PkHead = pkHead;						
			ConHead = conHead;			
			ConData = conData;			
		}

		
		public bool IsUIThread()
		{
			var pkType = (PACKET_TYPE)_conHead.PacketType;
			return MemPkDispatcher.IsMemPkUiThread(pkType);
		}
	}


	/// <summary>
	/// 압축 및 암호화된 패킷
	/// </summary>
	public struct EncMemPacketM
	{
		public TcpClient _tc;
		private FbsEncryptHeadM _pkEncHead;

		private byte[] _encData;
		private int _encDataLen; // 실제 사용 길이, ArrayPool에서 Rent함으로

		public EncMemPacketM(TcpClient tc, FbsEncryptHeadM pkEncHead, byte[] encData, int encDataLen)
		{
			_tc = tc;
			_pkEncHead = pkEncHead;
			_encData = encData;
			_encDataLen = encDataLen;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public MemPacketM MakeMemPacket(CompressAndEncryptM compEnc)
		{
			byte[] originData = _encData;
			if (_pkEncHead.IsCompress == 1)
			{
				originData = compEnc.Decompress(originData, _pkEncHead.OriginDataLen);
				_encDataLen = originData.Length;	// 압축 푼게 실제 데이터 사이트
			}

			originData = compEnc.Decrypt(originData, _encDataLen);   // decrypt 
			ReadOnlySpan<byte> spanOriginData = new ReadOnlySpan<byte>(originData);

			var viewBuffer = spanOriginData.Slice(0, PacketM.gPkHeadLen);			
			var pkHead = PacketM.DeserializePkHead(viewBuffer.ToArray());			
			spanOriginData = spanOriginData.Slice(PacketM.gPkHeadLen); // 읽은 만큼 버리기

			viewBuffer = spanOriginData.Slice(0, PacketM.gConHeadLen);			
			var pkConHead = PacketM.DeserializeContentHead(viewBuffer.ToArray());
			spanOriginData = spanOriginData.Slice(PacketM.gConHeadLen); // 읽은 만큼 버리기

			var conDataLen = pkConHead.ConDataLen;
			viewBuffer = spanOriginData.Slice(0, conDataLen);			

			return new MemPacketM(_tc, pkHead, pkConHead, viewBuffer.ToArray());

		}
	}


	/// <summary>
	/// 메모리 패킷에 대한 Dispatch 및 Action Funtion 세터 클래스
	/// </summary>
	public class MemPkDispatcher
	{

		// usort 패킷 타입과 액션을 가진 dictionary
		static Dictionary<PACKET_TYPE, AbMemPkAction> _dicMemPkAction = new();
		static Dictionary<PACKET_TYPE, bool> _dicIsMemPkUiThread = new();                  // LoadActions 할때 세팅되고, SendMemPk 할때 검사해서 있으면 UI ActionBlock쪽으로 보냄 


		/// <summary>
		/// memPk 액션 실행 함수
		/// </summary>
		/// <param name="memPk"></param>        
		static public async Task MemPkAction(MemPacketM memPk)
		{
			AbMemPkAction memPkAction;
			PACKET_TYPE packetType = (PACKET_TYPE)memPk.ConHead.PacketType;
			try
			{
				UserM user = memPk.U;
				if (user != null && AbNetworkBase.IsAllowedPacket(user, packetType) == false) // 패킷 검증
				{					
				
                    Debug.WriteLine($" 패킷 오류 - 현재 유저 PkState:{user.AllowedPkState.ToString()} 현재도착 패킷타입 번호:{packetType}");
					
					// 방지하는 차원으로 쓸수 있으므로 여기서 세분화해서 체크하던지 Exception 추후 풀어야 함
					throw new Exception($" 패킷 오류 - 현재 유저 PkState:{user.AllowedPkState.ToString()} 현재도착 패킷타입 번호:{packetType}");
                }

				if (_dicMemPkAction.TryGetValue(packetType, out memPkAction) == true)
				{
					await memPkAction.MemPkAction(memPk).ConfigureAwait(false);
				}
				else
				{
					//Debug.WriteLine($"MemPkAction등록된 패킷타입이 없음: null임:{((PACKET_TYPE)packetType).ToString()}");
					//Debug.WriteLine("하하~");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"MemPkAction등록된 패킷을 실행하다 오류남 패킷번호:{packetType}" + ex.Message);
				throw new Exception($"MemPkAction등록된 패킷을 실행하다 오류남 패킷번호:{packetType}" + ex.Message);
			}
			finally
			{
				
			}

		}


		public static bool IsMemPkUiThread(PACKET_TYPE packetType)
		{
			if (_dicIsMemPkUiThread.ContainsKey(packetType) == true)
				return true;

			return false;
		}


		/// <summary>
		/// 
		/// </summary>
		List<AbMemPkAction> _memPkActionList = new();
		public void Add(AbMemPkAction memPkAction)
		{
			_memPkActionList.Add(memPkAction);
		}

		/// <summary>
		/// 단 한번만 호출 할 것
		/// </summary>        
		public void LoadActions()
		{
			List<AbMemPkAction>.Enumerator er = _memPkActionList.GetEnumerator();

			while (er.MoveNext())
			{
				AbMemPkAction memPkAction = er.Current;
				_dicMemPkAction[memPkAction.PacketType] = memPkAction;    // memPkAction 딕셔너리 세팅

				if (memPkAction.bMemPkUiThread)  // 해당 패킷이 Ui Thread 쪽에서 실행되어야 하는 거면 
				{
					_dicIsMemPkUiThread[memPkAction.PacketType] = true;
				}
			}
		}
	}



	// MemPk 패킷에 대한 Action Function을 정의 하는 클래스 
	// eContentType이 추가 될 때 마다 반드시 상속 구현해야 됨
	public abstract class AbMemPkAction
	{
		private PACKET_TYPE _packetType;
		private bool _bMemPkUiThread = false;

		public AbMemPkAction(PACKET_TYPE packetType, bool bMemPkUIThread = false)   // bMemPkUIThread <-- UI 쓰레드에서 실행되어야 하는 MemPk이면 true
		{
			_packetType = packetType;
			_bMemPkUiThread = bMemPkUIThread;
		}

		public PACKET_TYPE PacketType { get => _packetType; }
		public bool bMemPkUiThread { get => _bMemPkUiThread; }

		// 필수 상속 구현
		public abstract Task MemPkAction(MemPacketM memPk);  // 실행자체가 동기적으로 이루어 져서 in으로 받아도 상관없다 MemPkDisPatcher

		// memPk분석해서 다시 응답 패킷 보낼 때 사용 (선택 구현)
		//protected virtual async Task ContentTypeActionSendPacketBack(in MemPacketM memPk) {; }
	}




}
