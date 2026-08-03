using System;
using System.IO;
using System.Text;

namespace EcsServerLibM
{
	public class FileWatcherSystemM : IDisposable
	{
		private FileSystemWatcher fileWatcher;
		private FileStream fileStream;
		private StreamReader streamReader;
		private long lastPosition = 0; // 마지막 읽은 위치 저장
		public string FilePath { get; private set; }

		// 텍스트 업데이트가 필요할 때 호출될 이벤트
		public event Action<string> OnFileChanged;

		public FileWatcherSystemM(string filePath)
		{
			FilePath = filePath;
			InitializeFileWatcher();
			InitializeFileStream();
		}

		public void SetEventHandler(Action<string> handler)
		{
			OnFileChanged += handler;
		}

		private void InitializeFileWatcher()
		{
			fileWatcher = new FileSystemWatcher
			{
				Path = Path.GetDirectoryName(FilePath),
				Filter = Path.GetFileName(FilePath),
				NotifyFilter = NotifyFilters.LastWrite
			};

			// 파일 변경 이벤트 핸들러 등록
			fileWatcher.Changed += (sender, e) => ReadNewLines();
			fileWatcher.EnableRaisingEvents = true;
		}

		private void InitializeFileStream()
		{
			// FileStream과 StreamReader 초기화
			fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			streamReader = new StreamReader(fileStream, Encoding.UTF8);

			// 파일의 끝으로 이동하여 마지막 읽은 위치 저장
			fileStream.Seek(0, SeekOrigin.End);
			lastPosition = fileStream.Position;
		}

		private void ReadNewLines()
		{
			// 현재 위치에서 파일 끝까지의 새 내용을 읽어오는 메서드
			try
			{
				fileStream.Seek(lastPosition, SeekOrigin.Begin); // 마지막 위치부터 읽기
				string newContent = streamReader.ReadToEnd(); // 새로운 내용 가져오기
				lastPosition = fileStream.Position; // 읽은 후의 위치를 마지막 위치로 업데이트

				// 새로 추가된 내용이 있으면 이벤트 발생
				if (!string.IsNullOrEmpty(newContent) && OnFileChanged != null)
				{
					OnFileChanged.Invoke(newContent);
				}
			}
			catch (IOException ex)
			{
				Console.WriteLine("파일을 읽는 도중 오류 발생: " + ex.Message);
			}
		}

		public void Dispose()
		{
			// 리소스 해제
			streamReader?.Dispose();
			fileStream?.Dispose();
			fileWatcher?.Dispose();
		}
	}
}
