using Google.FlatBuffers;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace EcsServerLibM
{
	/// <summary>
	/// 고성능 맵상 프로그레스 바 구현체
	/// </summary>
	/// 
	[Flags]
	public enum E_PROGRESS_BAR_UPDATE
	{ None = 0, Visible = 1, Gage = 2, Title = 4, BarText = 8, BarType = 16, Xy = 32, MaxGage = 64 }

	public abstract class AbProgressBarM : IDisposable
	{		
		
		// 상수 최적화		
		private const int DEFAULT_X = 400;
		private const int DEFAULT_Y = 900;
		

		protected E_PROGRESS_BAR_UPDATE _eUpdatePbar;

		// 값 타입으로 메모리 효율성 개선
		protected int _maxGage;
		public int Gage { get; private set; }
		public bool IsVisible { get; private set; } = true; 

		protected readonly StringBuilder _titleTextBuilder;
		protected readonly StringBuilder _barTextBuilder;
		protected BarConfiguration _config;
		private bool _disposed;

		public AbProgressBarM()
		{
			_titleTextBuilder = new StringBuilder(64); // 적절한 초기 용량
			_barTextBuilder = new StringBuilder(32);
			
		}
		/// <summary>
		/// 
		/// </summary>
		/// <param name="barType">1번부터 사용 함 (0번은 패킷에서 변화 없다는 의미로 사용)</param>
		/// <param name="x">-1이면 기본값 사용</param>
		/// <param name="y">-1이면 기본값 사용</param>
		/// <param name="title"></param>
		/// <param name="barText"></param>
		/// <param name="startGage"></param>
		/// <param name="maxGage"></param>
		/// <exception cref="ArgumentOutOfRangeException"></exception>		
		protected void Initialize(int barType, int x, int y, string title, string barText, int startGage, int maxGage)
		{
			if (maxGage >= 65535)
				throw new ArgumentOutOfRangeException(nameof(maxGage), "최대값 65535는 패킷에서 변경없음으로 씀"); // 최대값 65535는 패킷에서 변경없음으로 씀

			if (barType <= 0)   // 0번은 패킷에서 변화 없다는 의미로 사용
				throw new ArgumentOutOfRangeException(nameof(barType), "Bar type must be greater than 0.");
			x = (x == -1) ? DEFAULT_X : x; // -1이면 기본값 사용
			y = (y == -1) ? DEFAULT_Y : y; // -1이면 기본값 사용
			_config = new BarConfiguration(barType, x, y);

			IsVisible = true; // 기본적으로 보이도록 설정

			_maxGage = Math.Clamp(maxGage, 1, 65_534);	// ushort 최고값(65535)은 패킷 사이즈줄이기 위해 사용 함 
			Gage = Math.Clamp(startGage, 0, _maxGage);
			_eUpdatePbar = E_PROGRESS_BAR_UPDATE.None; // 초기화

			SetTitle(title.AsSpan()); // 초기 제목 설정
			SetBarText(barText.AsSpan()); // 초기 바 텍스트 설정
			
			_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.Visible | E_PROGRESS_BAR_UPDATE.Gage | E_PROGRESS_BAR_UPDATE.MaxGage | E_PROGRESS_BAR_UPDATE.BarType | E_PROGRESS_BAR_UPDATE.Xy;
			_SendUpdateUIPacket(); // 패킷 보냄
		}


		/// <summary>
		/// 배치 업데이트로 UI 리프레시 최소화
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Update(int newGage, ReadOnlySpan<char> barText = default, ReadOnlySpan<char> title = default)
		{			
			if (Gage != newGage)
			{
				Gage = Math.Clamp(newGage, 0, _maxGage);
				_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.Gage;
			}

			if (!title.IsEmpty && UpdateText(_titleTextBuilder, title))
				_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.Title;

			//if(barText.IsEmpty)
			//{
			//	barText = GetPercentString("진행률: ", true); // 기본 백분율 텍스트 설정
			//}

			if (!barText.IsEmpty && UpdateText(_barTextBuilder, barText))
				_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.BarText;
			_SendUpdateUIPacket(); // 패킷 보냄
			
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		void SetTitle(ReadOnlySpan<char> text)
		{
			if (UpdateText(_titleTextBuilder, text))
				_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.Title;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		void SetBarText(ReadOnlySpan<char> text)
		{
			if (UpdateText(_barTextBuilder, text))
				_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.BarText;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void SetVisible(bool isVisible)
		{
			if (IsVisible != isVisible) // Convert bool to int explicitly
			{
				IsVisible = isVisible; // Fix the type mismatch
				_eUpdatePbar |= E_PROGRESS_BAR_UPDATE.Visible;
			}
			_SendUpdateUIPacket(); // 패킷 보냄
		}
				

		/// <summary>
		/// 텍스트 변경 감지 및 효율적 업데이트
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool UpdateText(StringBuilder builder, ReadOnlySpan<char> newText)
		{
			if (builder.Length != newText.Length ||
				!builder.ToString().AsSpan().SequenceEqual(newText))
			{
				builder.Clear();
				builder.Append(newText);
				return true;
			}
			return false;
		}

		/// <summary>
		/// 실제 UI 업데이트 (가상 메서드로 오버라이드 가능)
		/// </summary>
		protected abstract void SendUpdateUIPacket(E_PROGRESS_BAR_UPDATE eUpdatePbar);
		
		void _SendUpdateUIPacket()
		{
			if (_eUpdatePbar == E_PROGRESS_BAR_UPDATE.None)
				return;
			SendUpdateUIPacket(_eUpdatePbar);
			_eUpdatePbar = E_PROGRESS_BAR_UPDATE.None; // 업데이트 플래그 초기화
		}

		public void Dispose()
		{
			if (!_disposed)
			{
				_titleTextBuilder?.Clear();
				_barTextBuilder?.Clear();
				_disposed = true;				
			}
		}

		virtual public void Clear()
		{
			_barTextBuilder?.Clear();
			_titleTextBuilder?.Clear();
			_eUpdatePbar = E_PROGRESS_BAR_UPDATE.None;
			_config = new BarConfiguration();

			Gage = 0;
			_maxGage = 0;
			IsVisible = true;
		}

		// 값 타입 구조체로 메모리 효율성 향상
		protected struct BarConfiguration
		{
			public int barType;
			public int x;
			public int y;


			public BarConfiguration(int barType, int x, int y)
			{
				this.barType = barType;
				this.x = x;
				this.y = y;
			}
		}

		/// <summary>
		/// 스택 할당을 사용한 효율적인 문자열 생성
		/// </summary>
		/// <summary>
		/// 스택 할당을 사용한 효율적인 백분율 문자열 생성
		/// </summary>
		public string GetPercentString(string preStr, bool bIncreseGage = true)
		{
			// 음수 처리
			if (Gage < 0 || _maxGage <= 0)
				return "0.00(%)";

			// 소수점 2자리까지 정확한 계산
			var totalPercent = Gage * 10000 / _maxGage;  // 0.01% 단위
			if(bIncreseGage == false) // 감소하는 gage라면 
			{
				totalPercent = 10000 - totalPercent; // 감소된 퍼센트 계산

			}
			var integerPart = totalPercent / 100;
			var decimalPart = totalPercent % 100;

			if (integerPart > 100) // 최대값 초과 처리
			{
				integerPart = 100;
				decimalPart = 0;
			}

			// 충분한 버퍼 크기 
			Span<char> buffer = stackalloc char[128];
			var written = 0;

			preStr.CopyTo(buffer); // 접두사 문자열 복사
			written += preStr.Length;

			// 정수 부분
			if (!integerPart.TryFormat(buffer[written..], out var intWritten))
				return "0.00(%)";
			written += intWritten;

			// 소수점
			buffer[written++] = '.';

			// 소수점 이하 2자리 (항상 2자리로 패딩)
			buffer[written++] = (char)('0' + decimalPart / 10);
			buffer[written++] = (char)('0' + decimalPart % 10);

			// 단위
			"(%)".CopyTo(buffer[written..]);
			written += 3;

			return buffer[..written].ToString();
		}

		static public string GetPercentString(string preStr, int gage, int maxGage, bool bIncreseGage = true)
		{
			// 음수 처리
			if (gage < 0 || maxGage <= 0)
				return "0.00(%)";

			// 소수점 2자리까지 정확한 계산
			var totalPercent = gage * 10000 / maxGage;  // 0.01% 단위
			if (bIncreseGage == false) // 감소하는 gage라면 
			{
				totalPercent = 10000 - totalPercent; // 감소된 퍼센트 계산

			}
			var integerPart = totalPercent / 100;
			var decimalPart = totalPercent % 100;

			if (integerPart > 100) // 최대값 초과 처리
			{
				integerPart = 100;
				decimalPart = 0;
			}

			// 충분한 버퍼 크기 
			Span<char> buffer = stackalloc char[128];
			var written = 0;

			preStr.CopyTo(buffer); // 접두사 문자열 복사
			written += preStr.Length;

			// 정수 부분
			if (!integerPart.TryFormat(buffer[written..], out var intWritten))
				return "0.00(%)";
			written += intWritten;

			// 소수점
			buffer[written++] = '.';

			// 소수점 이하 2자리 (항상 2자리로 패딩)
			buffer[written++] = (char)('0' + decimalPart / 10);
			buffer[written++] = (char)('0' + decimalPart % 10);

			// 단위
			"(%)".CopyTo(buffer[written..]);
			written += 3;

			return buffer[..written].ToString();
		}
	}



	/// <summary>
	/// 서버용 고성능 프로그레스 바 관리 클래스
	/// </summary>
	public sealed class ProgressBarM : AbProgressBarM
	{
		MapObjM _mapObj;


		/// <summary>
		/// 반드시 Initialize 메서드를 호출하여 초기화해야 합니다.
		/// </summary>
		/// <param name="mapObj"> 맵 object </param>
		/// <param name="barType">프로그래시브바 타입 - 클라랑 협의 1부터 사용</param>
		/// <param name="x">-1이면 디폴트 좌표 사용 - 클라랑 협의</param>
		/// <param name="y">-1이면 디폴트 좌표 사용 - 클라랑 협의</param>
		/// <param name="title">타이틀</param>
		/// <param name="barText">바 텍스트</param>
		/// <param name="startGage">시작 게이지</param>
		/// <param name="maxGage">최대 게이지</param>
		public void Initialize(MapObjM mapObj, int barType, int x, int y, string title, string barText, int startGage, int maxGage)
		{
			_mapObj = mapObj;
			base.Initialize(barType, x, y, title, barText, startGage, maxGage);
		}


		///////////////////////////////////////////////////////////////
		// 프로그래시브 바 IDL 정의
		//	table FbsProgressBar
		//	{
		//		barType : ubyte;            // 0은 변경 없다는 의미
		//		visible : bool = true;
		//		title : string;
		//		barText : string;
		//		gage : ushort = 65535;		// 65535는 변경이 없다는 의미
		//		maxGage : ushort = 65535;	// 65535는 변경이 없다는 의미
		//		x : ushort = 65535;			// 65535는 변경이 없다는 의미
		//		y : ushort = 65535;			// 65535는 변경이 없다는 의미
		//	}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected override void SendUpdateUIPacket(E_PROGRESS_BAR_UPDATE eUpdatePbar)
		{
			string title = null;
			string barText = null;
			bool visible = true;
			int gage = 65535;
			int maxGage = 65535;
			int barType = 0;
			int x = 65535;
			int y = 65535;

			if (eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.Title))
				title = _titleTextBuilder.ToString();

			if (eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.BarText))
				barText = _barTextBuilder.ToString();

			if (eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.Visible))
				visible = IsVisible;

			if(eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.Gage))
				gage = Gage;

			if (eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.MaxGage))
				maxGage = _maxGage;

			if (eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.BarType))
				barType = _config.barType;

			if(eUpdatePbar.HasFlag(E_PROGRESS_BAR_UPDATE.Xy))
			{
				x = _config.x;
				y = _config.y;
			}

			var data = new FsProgressBarFactory(visible, barType, x, y, title, barText, gage, maxGage).Serialize();
			_mapObj?.WriteSendBufferToMapUsers(PACKET_TYPE.PC_PROGRESS_BAR, data);
		}

		override public void Clear()
		{
			base.Clear();
			_mapObj = null; // MapObjM 참조 해제
		}

		/// <summary>
		/// 프로그래시브바 생성 및 관리 팩토리 클래스
		/// </summary>
		public static class ProgressBarFactory
		{
			private static readonly ConcurrentQueue<ProgressBarM> Pool = new();

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public static ProgressBarM GetProgressBar()
			{
				if (Pool.TryDequeue(out var bar))
				{
					return bar;
				}

				return new ProgressBarM();
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public static void ReturnToPool(ProgressBarM bar)
			{
				if (bar != null && Pool.Count < 100) // 풀 크기 제한
				{
					// 객체 상태 리셋
					bar.Clear();
					Pool.Enqueue(bar);
				}
				
			}
		}
	}
}
