using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	public class UdpLogReceiverM : IDisposable
	{
		readonly int port; // 로그를 받을 포트

		private bool disposed = false; // 객체가 Dispose 되었는지 추적

		CancellationTokenSource cts;

		// 로그 메시지 수신 이벤트
		public event Func<string, ValueTask> LogMessageReceived;

		public UdpLogReceiverM(int port)
		{
			//this.ip = ip;
			this.port = port;
			cts = new CancellationTokenSource();
		}

		// 비동기 방식으로 수신 서버 시작
		public async Task StartListeningAsync()
		{
			// UdpClient 객체 생성
			using var udpListener = new UdpClient(port);
			//IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(ip), port);

			try
			{
				while (true)
				{
					if (cts.Token.IsCancellationRequested)
						break;

					// 비동기 방식으로 UDP 패킷 수신
					UdpReceiveResult result = await udpListener.ReceiveAsync();

					// 수신한 데이터를 문자열로 변환
					string logMessage = Encoding.UTF8.GetString(result.Buffer);

					// 로그 메시지 수신 이벤트 호출
					await OnLogMessageReceived(this, logMessage).ConfigureAwait(false);
				}

				cts.Dispose();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error receiving data: {ex.Message}");
			}
			finally
			{
				cts.Dispose();
			}
		}

		public void Stop()
		{
			cts.Cancel();
		}

		// 로그 메시지 수신 이벤트 처리기
		protected virtual async Task OnLogMessageReceived(object sender, string logMsg)
		{
			// 이벤트가 등록된 경우 이벤트 처리기 실행
			if (LogMessageReceived != null)
				await LogMessageReceived(logMsg).ConfigureAwait(false);
		}

		// IDisposable 구현: 리소스 해제
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		// Dispose 패턴: 리소스를 해제
		protected virtual void Dispose(bool disposing)
		{
			if (!disposed)
			{
				if (disposing)
				{
					// 관리되는 리소스 해제

				}

				// 비관리 리소스 해제 작업이 필요하면 추가

				disposed = true;
			}
		}

		// 소멸자 (Finalize 메서드) 정의
		~UdpLogReceiverM()
		{
			Dispose(false);
		}
	}

	public class TcpLogRecieverM : IDisposable
	{
		private readonly int port;
		private TcpListener listener;
		private readonly ILog logM; // Log4Net 인터페이스


		// 이벤트 델리게이트 정의
		public delegate void LogReceivedEventHandler(string logMessage);

		// 이벤트 정의
		public event LogReceivedEventHandler LogReceived;

		public TcpLogRecieverM(int port)
		{
			this.port = port;

			// log4net 설정 로드
			XmlConfigurator.Configure(new FileInfo("log4netSrv.config"));
			logM = LogManager.GetLogger(typeof(TcpLogRecieverM).Name);
		}

		public async Task StartAsync()
		{
			try
			{
				IPAddress localAddr = IPAddress.Parse("127.0.0.1");

				//listener = new TcpListener(IPAddress.Any, port);
				listener = new TcpListener(localAddr, port);
				listener.Start();
				logM.Info($"Log server started on port {port}.");

				while (true)
				{
					try
					{
						var client = await listener.AcceptTcpClientAsync();
						_ = HandleClientAsync(client); // 비동기로 클라이언트 처리                        
					}
					catch (Exception ex)
					{
						logM.Error("Error accepting client connection.", ex);
					}
				}
			}
			catch (Exception ex)
			{
				logM.Error("Error starting log server.", ex);
				throw; // 예외를 다시 던져서 호출자가 알 수 있도록 함
			}
		}

		private async Task HandleClientAsync(TcpClient client)
		{
			try
			{
				using (client)
				using (var stream = client.GetStream())
				using (var reader = new StreamReader(stream, Encoding.UTF8))
				{
					string message;
					while ((message = await reader.ReadLineAsync()) != null)
					{
						logM.Info(message); // log4net으로 로그 기록

						// 이벤트 발생
						OnLogReceived(message);
						logM.Info($"클라 메세지 전송 - {message}.");
					}
				}
			}
			catch (IOException ex)
			{
				logM.Error("IO error while reading from client.", ex);
			}
			catch (Exception ex)
			{
				logM.Error("Unexpected error while handling client.", ex);
			}
		}

		// 이벤트 발생 메서드
		protected virtual void OnLogReceived(string logMessage)
		{
			LogReceived?.Invoke(logMessage);
		}

		public void Dispose()
		{
			listener?.Stop(); // 리스너 중지
			GC.SuppressFinalize(this); // 가비지 수집기에서 해당 객체의 최종화를 억제
		}
	}

	/// <summary>
	/// 소켓 어펜더 : AppenderSkeleton을 상속받아 만듬
	/// </summary>
	public class SocketAppenderM : AppenderSkeleton
	{
		public string RemoteAddress { get; set; }
		public int RemotePort { get; set; }

		private TcpClient _tcpClient;
		private StreamWriter _writer;

		protected override void Append(LoggingEvent loggingEvent)
		{
			try
			{
				if (_tcpClient == null || !_tcpClient.Connected)
				{
					_tcpClient = new TcpClient(RemoteAddress, RemotePort);
					_writer = new StreamWriter(_tcpClient.GetStream()) { AutoFlush = true };
				}

				var logMessage = RenderLoggingEvent(loggingEvent);
				_writer.WriteLine(logMessage);
			}
			catch (Exception ex)
			{
				ErrorHandler.Error("Error sending log message over TCP", ex);
			}
		}

		protected override void OnClose()
		{
			_writer?.Close();
			_tcpClient?.Close();
			base.OnClose();
		}
	}


}
