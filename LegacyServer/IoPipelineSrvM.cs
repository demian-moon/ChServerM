using FbsClassM;
using Google.FlatBuffers;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.XPath;

namespace EcsServerLibM
{

	//public class MemPkFactoryForServer : AbMemPkFactory
	//{
	//	protected InnerSrvUserM _srvUser;
	//	ServerM _serverM;

	//	public MemPkFactoryForServer(ServerM serverM, TcpClient tc, CancellationTokenSource cts) : base(tc, cts)
	//	{
	//		_serverM = serverM;
	//	}

	//	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	//	public override async ValueTask SendMemPk(MemPacketM memPk)
	//	{
	//		await _serverM.SendMemPk(memPk, Cts).ConfigureAwait(false);

	//	}

	//	[MethodImplAttribute(MethodImplOptions.AggressiveInlining)]
	//	public override async ValueTask SendEncMemPk(EncMemPacketM encMemPk)
	//	{
	//		await _serverM.SendEncMemPk(encMemPk, Cts).ConfigureAwait(false);
	//	}
	//}


	/// <summary>
	/// IoPipe Write쪽에서 쓰는 서버 쪽 disconnect Process 클래스
	/// </summary>
	public class ServerDisconnectProcess : AbDisconnectProcess
	{
		ServerM _serverM;

		bool _disconnected = false;

		public ServerDisconnectProcess(ServerM serverM)
		{
			name = "server";
			_serverM = serverM;
		}
		override public async ValueTask DisconnectProcess(TcpClient tc)
		{			
						
			SrvUserM srvUser = SrvGlobal.GetUser(tc);
			if (srvUser.IsExist)
			{
				_serverM.DecrementServerUserCnt(); // 서버 유저 숫자 줄이기 

				await srvUser.DisconnectProcess().ConfigureAwait(false);    // 서버유저가 가진 자원들 모두 지우기
				_serverM.AppUserFinish(srvUser);   // 앱에서 앱유저 지우고, 게임중이었다면 관련 리소스 해제
				SrvGlobal.RemoveUser(tc);    // dicSrvUsers에서  서버유저를 지움

				Debug.WriteLine($"### 로긴후에 와서 SrvUser 바로 처리 {srvUser.Oid} ###");
			}
			else
			{
				CompressAndEncryptManM.TryRemove(tc, out CompressAndEncryptM _); // 혹시 살아 있을 수 있는 글로벌 암호화 객체 삭제 (유저 접속 완료 되기 전 Tc close되면)

				///////////// 이 시점에 패킷 주고 받고 있는 상황 일 수 있어서 무조건 특정 시간 후에 처리한다 //////////
				ServerM.gDisconnectTimer.AddOrUpdateTimer(tc, new TimerM_SrvUser_Delay_Disconnect(_serverM, tc), TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
			}
		}
	}

	/// <summary>
	/// System.Io.Pipeline을 이용하는 클래스 
	/// </summary>
	/// 
	public class IoPipelineSrvM
	{

		/// <summary>
		/// 파이프라인을 이용한 고성능 네트워크 스트림 Async Read와 이에 따른 처리 관련 함수
		/// System.IO.Pipelines Nuget 패키지
		/// </summary>
		/// <param name="tc"> TcpClient </param>

		// 서버용 파이프 라인 (MemPk 만든후 저장하는 방식이 클라와 다름)
		static public async Task PipelineForServerAsync(TcpClient tc, ServerM serverM)
		{
			CancellationTokenSource cts = new CancellationTokenSource();


			PipeOptions pipeOption = new(null, null, null, -1, -1, -1, false); // 스레드 풀에서 돌린다. 

			Pipe pipe = new(pipeOption);
			Task pipeWritingTask = SrvFillPipeAsync(tc, pipe.Writer, cts, new ServerDisconnectProcess(serverM));
			Task pipeReadingTask = SrvReadPipeAsync(tc, pipe.Reader, cts, serverM);

			await Task.WhenAll(pipeWritingTask, pipeReadingTask).ConfigureAwait(false);
			tc?.Close();
			tc?.Dispose();

		}


		static public async Task SrvFillPipeAsync(TcpClient tc, PipeWriter pipeWriter, CancellationTokenSource cts, AbDisconnectProcess disCon)
		{
			const int minBufferSize = 512;
			NetworkStream netStream = tc.GetStream();

			CancellationToken ct = cts.Token;

			while (true)
			{
				try
				{
					Memory<byte> memory = pipeWriter.GetMemory(minBufferSize);

					try
					{
						int nReadByte = await netStream.ReadAsync(memory, ct).ConfigureAwait(false);

						if (nReadByte == 0)
						{
							// EOF - 핀 받을 때
							cts.Cancel();
							await disCon.DisconnectProcess(tc).ConfigureAwait(false);
							Debug.WriteLine("디스커넥트 프로스세스 완료~~~" + disCon.name);
							break;
						}
						pipeWriter.Advance(nReadByte); // pipeWriter에게 얼마나 읽었는지 알려주는 함수
					}
					catch (OperationCanceledException ex)
					{
						if (ex.CancellationToken.IsCancellationRequested)
						{
							await disCon.DisconnectProcess(tc).ConfigureAwait(false);
							Debug.WriteLine("나 Cancellation 예외로 빠져나왔지롱~~:" + disCon.name);
						}
						break;
					}
					catch (Exception ex)
					{
						cts.Cancel();
						await disCon.DisconnectProcess(tc).ConfigureAwait(false);
						Debug.WriteLine($"뭔일이래!! 예외로 나옴 {ex.Message}");
						break;
					}
				}
				catch (Exception ex)
				{
					cts.Cancel();
					await disCon.DisconnectProcess(tc).ConfigureAwait(false);
					Debug.WriteLine($"나 여기 pipeWriter.GetMemory 예외로 빠져나왔지롱~~:{disCon.name} : {ex.Message}");
					break;
				}

				FlushResult result = await pipeWriter.FlushAsync(ct).ConfigureAwait(false); // pipeReader가 읽은 바이트를 사용하게 한다.
				if (result.IsCompleted || result.IsCanceled)     // 혹시나 pipeReader.Complete()가 먼저 불렸을 때 종료 되도록 
					break;
			}

			//////////////////////////////////////////////
			// Disconnect시 처리 /////////////////////////
			//////////////////////////////////////////////
			try
			{
				// 소켓 shutdown
				if (tc.Client != null && tc.Client.Connected)
				{
					tc.Client.Shutdown(SocketShutdown.Both);
					Debug.WriteLine("소켓 열려있어서 닫음 룰루~~");
				}
			}			
			catch (Exception ex)
			{
				Debug.WriteLine("소켓 shutdown하다가 익셉션이야 - 아마도 rst??");
			}

			try
			{
				netStream?.Close();
				Debug.WriteLine("올~~ 정상 종료:" + disCon.GetName());
			}
			catch (Exception e)
			{
				Debug.WriteLine($"헉~~ 비정상 종료: 띠로리~~~링 {disCon.GetName()} - {e.Message}");
			}
			finally
			{
				try
				{
					netStream?.Dispose();
					if (CompressAndEncryptManM.TryRemove(tc, out CompressAndEncryptM _) == true) // 혹시 살아 있을 수 있는 글로벌 암호화 객체 삭제 (추후 삭제 할 것)
					{
						Debug.WriteLine("글로벌 암호화 객체가 아직도 살아 있었음!! 띠옹~~ ");
					}

					tc?.Close();
				}
				catch (Exception cleanupEx)
				{
					Debug.WriteLine($"서버 넷스트림 정리 작업 중 오류: {cleanupEx.Message}");
				}
			}

			await pipeWriter.CompleteAsync().ConfigureAwait(false); // 더이상 들어오는 데이터가 없음을 pipeReader에게 알려준다                                   

		}

		/// <summary>
		/// 읽기 보류 중에 CancellationToken이 취소되는 경우 OperationCanceledException이 throw됩니다.
		//  PipeReader.CancelPendingRead을 통해 현재 읽기 작업을 취소하는 방법을 지원하여 예외 증가를 방지합니다.PipeReader.CancelPendingRead를 호출하면
		//  PipeReader.ReadAsync에 대한 현재 또는 다음 호출이 IsCanceled가 true로 설정된 ReadResult를 반환합니다.
		//  이는 기존 읽기 루프를 비파괴적이고 예외 없는 방식으로 중지하는 데 유용할 수 있습니다.
		/// </summary>
		/// <param name="pipeReader"></param>
		/// <returns></returns>      
		enum eToReadState { PK_HEAD, CONTENT_HEAD, CONTENT_DATA, ENC_PK_HEAD, ENC_PK_DATA }
		static public async Task SrvReadPipeAsync(TcpClient tc, PipeReader pipeReader, CancellationTokenSource cts, ServerM serverM) // 읽은 헤더 사이즈
		{
			try
			{
				FbsPkHeadM _pkHead = default;
				FbsContentHeadM _conHead = default;
				FbsEncryptHeadM _encPkHead = default;     // 압축(암호화) 헤더				

				eToReadState _toReadState = eToReadState.PK_HEAD; // 0 pkHead, 1 contentHead, 2 contentData 읽을 차례

				long _toReadDataLen = 0;
				long _curReadDataLen = 0;

				_toReadDataLen = PacketM.gPkHeadLen;  // 체일 처음 읽을 데이터 길이   
				CancellationToken ct = cts.Token;								

				byte[] pooledEncHeadBuf = null;
				byte[] pooledEncDataBuf = null;

				while (true)
				{
					// 두가지를 result로 알려주는데 하나는 buffer와 IsCompleted 이다
					// NetworkStream에서 pipeWriter가 읽기 때문에 nReadByte가 0이면 break문에 의해서 pipeWriter.Complete() 함수로 알려준다
					ReadOnlySequence<byte> buffer = default;
					ReadResult result = default;			
											
					try
					{
						result = await pipeReader.ReadAsync(ct).ConfigureAwait(false); // pipeWriter가 FlushAsync() 하기까지 대기, 
						buffer = result.Buffer; // 해당 버퍼를 얻어온다


						if (result.IsCanceled)
							break;

						// 할수 있는 만큼 파싱해서 패킷을 만든다                                        
						while (true)
						{
							if (buffer.Length < _toReadDataLen)
								break;

							var viewBuffer = buffer.Slice(0, _toReadDataLen);

							if (_toReadState == eToReadState.ENC_PK_HEAD)
							{
								pooledEncHeadBuf = ArrayPool<byte>.Shared.Rent((int)_toReadDataLen);
								viewBuffer.CopyTo(pooledEncHeadBuf);

								_encPkHead = FbsEncryptHeadM.GetRootAsFbsEncryptHeadM(new ByteBuffer(pooledEncHeadBuf));
								_curReadDataLen = _toReadDataLen;

								_toReadState = eToReadState.ENC_PK_DATA;
								_toReadDataLen = _encPkHead.EncDataLen;
							}
							else if (_toReadState == eToReadState.ENC_PK_DATA)
							{
								// 암호화 패킷 완료
								pooledEncDataBuf = ArrayPool<byte>.Shared.Rent((int)_toReadDataLen);
								viewBuffer.CopyTo(pooledEncDataBuf);
								
								var encMemPk = new EncMemPacketM(tc, _encPkHead, pooledEncDataBuf, (int)_toReadDataLen);

								await serverM.SendEncMemPk(encMemPk, cts).ConfigureAwait(false); // 압축 암호화된 MemPk를 전송
								ArrayPool<byte>.Shared.Return(pooledEncHeadBuf);
								ArrayPool<byte>.Shared.Return(pooledEncDataBuf);

								_curReadDataLen = _toReadDataLen;

								_toReadState = eToReadState.ENC_PK_HEAD;
								_toReadDataLen = PacketM.gEncHeadLen;

							}
							else if (_toReadState == eToReadState.PK_HEAD)
							{								
								_pkHead = PacketM.DeserializePkHead(viewBuffer.ToArray());
								_curReadDataLen = _toReadDataLen;

								if (PacketM.IsValidCheckSum(_pkHead) == true) // 첵섬이 올바를 때
								{
									_toReadState = eToReadState.CONTENT_HEAD;
									_toReadDataLen = _pkHead.ConHeadLen; // 다음 읽은 데이터 사이즈
								}
								else
								{
									throw new Exception();  // 첵섬이 이상하면 예외 스로우
								}
							}
							else if (_toReadState == eToReadState.CONTENT_HEAD)
							{
								_toReadState = eToReadState.CONTENT_DATA;								

								_conHead = PacketM.DeserializeContentHead(viewBuffer.ToArray());
								_curReadDataLen = _toReadDataLen;

								_toReadDataLen = _conHead.ConDataLen; // 다음읽을 데이터 사이즈
								if (_toReadDataLen == 0) // 컨텐츠 데이터가 null이면 
								{
									var memPk = new MemPacketM(tc, _pkHead, _conHead, viewBuffer.ToArray());									
									await serverM.SendMemPk(memPk, cts).ConfigureAwait(false);									

									// 추후 암호화 패킷 이전에 다른 패킷들이 추가 되면 추가하는 것을 고려
									//var encryptStart = CompressAndEncManM.IsExistCompEnc(Tc);
									//if (encryptStart) // 이 후 부터 압축 및 암호화 패킷 (Server = PS_LOGIN_FIN, Client = PC_LOGIN_OK)
									//{
									//    _toReadState = eToReadState.ENC_PK_HEAD;
									//    _toReadDataLen = PacketM.gEncHeadLen;
									//}
									//else
									//{
									_toReadState = eToReadState.PK_HEAD;
									_toReadDataLen = PacketM.gPkHeadLen;
									//}
								}

							}
							else if (_toReadState == eToReadState.CONTENT_DATA)
							{
								var memPk = new MemPacketM(tc, _pkHead, _conHead, viewBuffer.ToArray());    // 컨텐츠 데이터를 넘김								
								await serverM.SendMemPk(memPk, cts).ConfigureAwait(false);

								_curReadDataLen = _toReadDataLen;
								///////////////////////////////////////////////////////////////

								var encryptStart = (_conHead.PacketType == (ushort)PACKET_TYPE.PSC_COMP_ENC_CHANGE);
								if (encryptStart) // 이 후 부터 압축 및 암호화 패킷 (Server = PS_LOGIN_FIN, Client = PC_LOGIN_OK)
								{

									_toReadState = eToReadState.ENC_PK_HEAD;
									_toReadDataLen = PacketM.gEncHeadLen;
								}
								else
								{
									_toReadState = eToReadState.PK_HEAD;
									_toReadDataLen = PacketM.gPkHeadLen;
								}

							}
							buffer = buffer.Slice(_curReadDataLen); // 버퍼 읽은 만큼 없애기 

						}


						if (result.IsCompleted) // 더이상 들어올 데이터가 없다면
							break;
					}
					catch (OperationCanceledException oce)
					{
						if (oce.CancellationToken.IsCancellationRequested)
						{
							Debug.WriteLine($"SrvReadPipeAsync PipeRead중 취소됨 : {oce.Message}");							
						}
						break;
					}
					catch (InvalidDataException ide)
					{
						// 데이터 파싱 중 잘못된 데이터에 대한 처리
						Debug.WriteLine($"SrvReadPipeAsync 데이터 파싱 중 오류 발생: {ide.Message}");
						// 필요에 따라 추가적인 작업을 수행할 수 있음
					}
					catch (Exception ex)
					{
						// 다른 예외 처리
						Debug.WriteLine($"SrvReadPipeAsync 예외 발생: {ex.Message}");
						// 예외 처리 로직 추가
					}
					finally
					{
						// 우리가 소비한 데이터를 pipeReader에게 알려준다. (함수 자체는 쓰고 남은 남은 개수를 알려준다) 
						// 이 이후에 메모리 액세스 하면 안됨!!!!
						if(!result.Equals(default) )
						{
							pipeReader.AdvanceTo(buffer.Start, buffer.End);
						}
					}
				}
			}
			catch (Exception ex)
			{
				// 최상위 예외 처리
				Debug.WriteLine($"SrvReadPipeAsync 서버 읽기 중 예외 발생: {ex.Message}");
				// 예외 처리 로직 추가
			}
			finally
			{
				await pipeReader.CompleteAsync().ConfigureAwait(false);
			}
		}
	}


}
