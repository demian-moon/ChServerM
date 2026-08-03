using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	// Euckr 인코딩 
	public static class EncodingHelperM
	{
		static EncodingHelperM()
		{
			// 프로그램 실행 중 한 번만 등록됨
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		}

		public static Encoding EucKr
		{
			get { return Encoding.GetEncoding("euc-kr"); }
		}
	}

	public class FileM : IDisposable
	{

		private string filePath;
		private FileStream fileStream;
		private StreamWriter streamWriter;
		private List<string> writeBuffer;
		private const int BufferSizeLimit = 100; // 버퍼 크기 (라인 수)
		private System.Threading.Timer flushTimer; // 스레드 타이머
		private const int FlushInterval = 5000; // 5초 (단위: 밀리초)
		private bool isFlushing; // 플러시 중인지 여부

		public FileM(string path)
		{
			filePath = path;
			writeBuffer = new List<string>();
			InitializeFileStream();
			InitializeTimer();
		}

		private void InitializeFileStream()
		{
			fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
			streamWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = false };
			fileStream.Seek(0, SeekOrigin.End);
		}

		private void InitializeTimer()
		{
			flushTimer = new System.Threading.Timer(OnFlushTimerElapsed, null, FlushInterval, Timeout.Infinite);
		}

		private async void OnFlushTimerElapsed(object state)
		{
			await FlushBufferAsync();
		}

		public async Task WriteLineAsync(string content)
		{
			writeBuffer.Add(content);

			if (writeBuffer.Count >= BufferSizeLimit)
			{
				await FlushBufferAsync().ConfigureAwait(false);
			}
			else
			{
				flushTimer.Change(FlushInterval, Timeout.Infinite); // 타이머 리셋
			}
		}

		private async Task FlushBufferAsync()
		{
			if (isFlushing) return; // 플러시 중이면 리턴
			isFlushing = true;

			if (writeBuffer.Count > 0)
			{
				foreach (var line in writeBuffer)
				{
					await streamWriter.WriteLineAsync(line).ConfigureAwait(false);
				}

				await streamWriter.FlushAsync().ConfigureAwait(false);
				writeBuffer.Clear();
			}

			isFlushing = false;
		}

		public async Task SaveFileAsync(Encoding encoding)
		{
			await FlushBufferAsync();

			streamWriter?.Dispose();
			fileStream?.Dispose();

			string content;
			using (var reader = new StreamReader(filePath, Encoding.UTF8))
			{
				content = await reader.ReadToEndAsync().ConfigureAwait(false);
			}

			using (var writer = new StreamWriter(filePath, false, encoding))
			{
				await writer.WriteAsync(content).ConfigureAwait(false);
			}

			InitializeFileStream();
		}

		public async Task FlushAsync()
		{
			await FlushBufferAsync().ConfigureAwait(false);
		}

		public void Dispose()
		{
			FlushBufferAsync().GetAwaiter().GetResult();

			streamWriter?.Dispose();
			fileStream?.Dispose();

			flushTimer?.Dispose();
		}

		/// <summary>
		/// 윈도우 기본(utf-8)로 저장함
		/// </summary>
		/// <param name="filePath"></param>
		/// <param name="data"></param>
		static async ValueTask WriteStringAsync(string filePath, string data, Encoding encoding)
		{
#if NET
			try
			{
				await File.WriteAllTextAsync(filePath, data, encoding);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message);
			}

#else
			File.WriteAllText(filePath, data, encoding);
#endif
			//using FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
			//using BufferedStream bs = new BufferedStream(fs);

			////byte[] buffer = Encoding.Default.GetBytes(data);
			//Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			//byte[] buffer = Encoding.GetEncoding(949).GetBytes(data);            

			//await bs.WriteAsync(buffer, 0, buffer.Length);   
			//await bs.FlushAsync();
		}

		static public async ValueTask WriteStringUTF8Async(string filePath, string data)
		{
			await WriteStringAsync(filePath, data, Encoding.UTF8).ConfigureAwait(false);
		}

		static public async ValueTask WriteStringEucKrAsync(string filePath, string data)
		{
			var encoding = EncodingHelperM.EucKr;
			await WriteStringAsync(filePath, data, encoding).ConfigureAwait(false);
		}
		

		/// <summary>
		/// utf-8 파일 또는 BOM이 있는 파일만 열수 있으니 주의 할 것!! 
		/// euc-kr 안됨!!!
		/// </summary>
		/// <param name="filePath"></param>
		/// <returns></returns>
		static public async Task<string> ReadStringAsync(string filePath)
		{
#if NET && !NETFRAMEWORK
			var text = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
#else
			var text = File.ReadAllText(filePath);

			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				// 파일 내용을 읽기 위한 StreamReader를 생성합니다.
				using (StreamReader reader = new StreamReader(fileStream, Encoding.UTF8))
				{
					text = await reader.ReadToEndAsync().ConfigureAwait(false);
				}
			}
#endif
			return text;


			//using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read); // using으로 리소스 해제
			//using var bfs = new BufferedStream(fs);
			//using var streamReader = new StreamReader(fs, true);

			//string readLine;
			//StringBuilder __sb = new StringBuilder();
			//while ((readLine = await streamReader.ReadLineAsync()) != null)
			//{
			//    __sb.AppendLine(readLine);
			//}

			//return __sb.ToString();
		}

		static public string ReadString(string filePath)
		{
			var text = File.ReadAllText(filePath);

			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				// 파일 내용을 읽기 위한 StreamReader를 생성합니다.
				using (StreamReader reader = new StreamReader(fileStream, Encoding.UTF8))
				{
					text = reader.ReadToEnd();
				}
			}
			return text;
		}



		static public async Task<string> ReadStringAsync(string filePath, Encoding encoding, CancellationToken ct = default)
		{
#if NET
			var text = await File.ReadAllTextAsync(filePath, encoding, ct).ConfigureAwait(false);
#else
			var text = File.ReadAllText(filePath, encoding);
#endif
			return text;
		}


		public static string ConvertEucKrToUTF8(string eucKrStr)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			Encoding encodeEucKr = Encoding.GetEncoding("EUC-KR");
			var eucKrBytes = encodeEucKr.GetBytes(eucKrStr);

			byte[] utf8Bytes = Encoding.Convert(encodeEucKr, Encoding.UTF8, eucKrBytes);
			string utf8String = Encoding.UTF8.GetString(utf8Bytes);

			return utf8String;
		}


		// 상대 경로
		public static string[] GetFileNames(string relativeFilePath)
		{
			// 현재 실행 폴더 경로
			string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

			// 디렉토리 경로 얻기
			string directoryPath = Path.GetDirectoryName(relativeFilePath);

			// "Script" 폴더 경로
			string directory = Path.Combine(currentDirectory, directoryPath);

			string fileName = Path.GetFileName(relativeFilePath);

			// "Script" 폴더가 존재하는지 확인
			if (Directory.Exists(directory))
			{
				// .cs 파일 이름 목록 반환
				var filePaths = Directory.GetFiles(directory, fileName);
				string[] fileNames = new string[filePaths.Length];

				for (int i = 0; i < filePaths.Length; i++)
				{
					fileNames[i] = Path.GetFileName(filePaths[i]); // 파일 이름만 추출
				}

				return fileNames;
			}
			else
			{
				throw new ArgumentException($"{directory} 폴더가 존재하지 않습니다.");
			}
		}

		public static string[] GetFilePathNames(string relativeFilePath)
		{
			// 현재 실행 폴더 경로
			string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

			// 디렉토리 경로 얻기
			string directoryPath = Path.GetDirectoryName(relativeFilePath);

			// "Script" 폴더 경로
			string directory = Path.Combine(currentDirectory, directoryPath);

			string fileName = Path.GetFileName(relativeFilePath);

			// "Script" 폴더가 존재하는지 확인
			if (Directory.Exists(directory))
			{
				// .cs 파일 이름 목록 반환
				return Directory.GetFiles(directory, fileName);
			}
			else
			{
				throw new ArgumentException($"{directory} 폴더가 존재하지 않습니다.");
			}
		}

		/// <summary>
		/// 디렉토리 카피
		/// </summary>
		/// <param name="sourceDir"></param>
		/// <param name="destinationDir"></param>
		/// <param name="bOverwrite"></param>
		public static void CopyDirectory(string sourceDir, string destinationDir, bool bSubDirCopy = true, bool bOverwrite = true)
		{
			// 대상 디렉토리가 없으면 생성
			if (!Directory.Exists(destinationDir))
			{
				Directory.CreateDirectory(destinationDir);
			}

			// 파일 복사
			foreach (string filePath in Directory.GetFiles(sourceDir))
			{
				string fileName = Path.GetFileName(filePath);
				string destinationFilePath = Path.Combine(destinationDir, fileName);
				File.Copy(filePath, destinationFilePath, bOverwrite); // 덮어쓰기를 원하면 true로 설정
			}

			// 하위 디렉토리 복사
			if (bSubDirCopy)
			{
				foreach (string directoryPath in Directory.GetDirectories(sourceDir))
				{
					string directoryName = Path.GetFileName(directoryPath);
					string destinationSubDir = Path.Combine(destinationDir, directoryName);
					CopyDirectory(directoryPath, destinationSubDir); // 재귀적으로 호출
				}
			}

		}
	}
}
