using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Repository.Hierarchy;
using log4net.Util;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EcsServerLibM
{

	/// <summary>
	/// 로그 추상 객체
	/// </summary>
	public abstract class AbLogM<T>
	{
		
		abstract public void WriteAsync(T msg); // 로그 쓰기
		
		abstract public void FlushLogs();

		abstract public void Debug(T msg);
	}


	public class Log4NetM : AbLogM<string>
	{
		ILog logM = null;
		
        public Log4NetM(string loggerName, string configFileName = null, IPAddress ipUdpAppender = null)
		{			
			//bool isConfigured = LogManager.GetRepository().Configured;
			string log4netConfigFile = "log4net.config";
			log4netConfigFile = configFileName ?? log4netConfigFile;

			if (File.Exists(log4netConfigFile) == false)
			{
				System.Diagnostics.Debug.WriteLine($"log4net.config 파일이 없습니다. {log4netConfigFile}"); // TODO: 로그로 변경
				return;
			}

			XmlConfigurator.Configure(new FileInfo(log4netConfigFile));

			// UdpAppender Ip동적 설정
			if (ipUdpAppender != null)
			{
				var hierarchy = (Hierarchy)LogManager.GetRepository();
				var udpAppender = hierarchy
					.GetAppenders()
					.OfType<UdpAppender>()
					.FirstOrDefault(a => a.Name == "UdpAppender");

				if (udpAppender != null)
				{
					// 3) RemoteAddress 설정
					udpAppender.RemoteAddress = ipUdpAppender;

					// 4) 변경사항 반영
					udpAppender.ActivateOptions();
				}
			}

			logM = LogManager.GetLogger(loggerName);		
		}

		public Log4NetM(string loggerName, string unityStreamingAssetPath)
		{

#if UNITY_IOS || UNITY_ANDROID
			return;
# endif
			string configPath = Path.Combine(unityStreamingAssetPath, "log4netCla.config");
			if (File.Exists(configPath) == false)
			{
				System.Diagnostics.Debug.WriteLine($"log4net.config 파일이 없습니다. {configPath}"); // TODO: 로그로 변경
				return;
			}

			var configFile = new FileInfo(configPath);
			XmlConfigurator.Configure(configFile);
			logM = LogManager.GetLogger(loggerName);
		}


		/// <summary>
		/// 버퍼링된 파일로그등을 최종적으로 Flush한다. 
		/// </summary>		
		public override void FlushLogs()
		{
			if(logM == null)
			{				
				return;
			}

			// 버퍼링된 로그를 강제로 flush
			var appenders = LogManager.GetRepository().GetAppenders();
			foreach (var appender in appenders)
			{
				if (appender is log4net.Appender.BufferingAppenderSkeleton bufferingAppender)
				{
					bufferingAppender.Flush();
				}
			}
		}

		public override void Debug(string msg)
		{
			if (logM == null)
			{
				return;
			}

			logM.Debug(msg);
		}
				
		public override void WriteAsync(string msg)
		{
			if (logM == null)
			{
				return;
			}

			try
			{
				logM.Debug(msg);
			}
			catch (Exception e)
			{
				System.Diagnostics.Debug.WriteLine($"로그 에러: {e.Message}");
			}
						
		}

		public static void LoadLoggerForUnity(string loggerName, string unityStreamingAssetPath)
		{			
			
		}
	}



	public class FileLogM : IDisposable
	{
		FileM _fileM;
		public FileLogM(string filePath)
		{
			_fileM = new FileM(filePath);
		}

		async Task Write(string contents)
		{
			await _fileM.WriteLineAsync(contents).ConfigureAwait(false);
		}

		public void Dispose()
		{
			_fileM.Dispose();
		}
	}
}
