using System.Text.RegularExpressions;


namespace EcsServerLibM
{

	public interface IStringAnalyzerM
	{
		public string Analyze();
	}

	/// <summary>
	/// StringAnalyzerM 가장 기본이 되는 소스 문자열 클래스
	/// 이후 데코레이터로 감싸는 클래스는 감싸는 순서대로 실행됨을 유의 할 것 !
	/// </summary>
	public class StringAnalyzerM : IStringAnalyzerM
	{
		public const string SPACE_CHARS = @"[^\S\n]+";

		string _text;

		public string Analyze()
		{
			return _text;
		}


		public StringAnalyzerM(string text)
		{
			_text = text;
		}

		// static 함수들  모임

	}

	/// <summary>
	/// StringAnalyzerM의 데코레이션 패턴
	/// </summary>
	public abstract class AbStringAnalyzerDecoM : IStringAnalyzerM
	{
		protected IStringAnalyzerM _sam;
		public abstract string Analyze();
		
	}


	/// <summary>
	/// 정규식으로 매칭되는 내용을 모두 지우는 클래스
	/// </summary>
	public class RegExRemoveAnalyzerM : AbStringAnalyzerDecoM
	{		
		string _regEx;

		public RegExRemoveAnalyzerM(IStringAnalyzerM sam, string regEx)
		{
			_sam = sam;
			_regEx = regEx;			
		}

		public override string Analyze()
		{
			var text = _sam.Analyze();
			var removeRegEx = Regex.Replace(text, _regEx, "");
			return removeRegEx;
		}
	}


	/// <summary>
	/// 코멘트 (//, /* */)을 없애준다
	/// 1. RegexOptions.None (기본 옵션)
	/// .(dot) 은 개행 문자(\n)를 포함하지 않음.
	/// ^와 $는 문자열의 처음과 끝만 매칭.
	/// 
	/// 2. RegexOptions.Multiline
	/// .(dot) 은 여전히 개행을 포함하지 않음.
	/// ^와 $가 각 줄(line) 의 시작과 끝을 매칭.
	/// 
	/// 3. RegexOptions.Singleline
	/// .(dot) 이 개행을 포함하여 모든 문자(\n 포함)를 매칭.
	/// 하지만 ^와 $의 동작은 변경되지 않음.

	/// </summary>
	public class CommentStringAnalyzerM : AbStringAnalyzerDecoM
	{		

		public CommentStringAnalyzerM(IStringAnalyzerM sam)
		{
			_sam = sam;
		}

		public override string Analyze()
		{
			var text = _sam.Analyze();
			string noSingleLineComments = Regex.Replace(text, @"//.*", "");
			string noMultiLineComments = Regex.Replace(noSingleLineComments, @"/\*.*?\*/", "", RegexOptions.Singleline);

			return noMultiLineComments; ;

		}
	}

	/// <summary>
	/// 특정 문자열 이후 모두 지우기
	/// </summary>
	public class RmStrAfterAnalyzerM : AbStringAnalyzerDecoM
	{
		string _specificStr;

		public RmStrAfterAnalyzerM(IStringAnalyzerM sam, string specificStr)
		{
			_sam = sam;
			_specificStr = specificStr;
		}

		public override string Analyze()
		{
			var text = _sam.Analyze();
			string removeStrAfter = Regex.Replace(text, @$"{_specificStr}.*", "");
			return removeStrAfter;

		}
	}

	/// <summary>
	/// 연속으로 있는 여러개의 문자열 하나로 만들기 (탭 구분자 여러개 1개로 만들때 유용함)
	/// 공백문자를 정의 할 때 정규식 : @"[^\S\n]+" - 공백 문자지만, 개행문자는 아닌 것
	/// </summary>
	public class DuplicateStringAnalyzerM : AbStringAnalyzerDecoM
	{		
		string _duplicateSeparator; // 중복 제거할 문자열

		public DuplicateStringAnalyzerM(IStringAnalyzerM sam, string duplicateSeparator = "\t")
		{
			_sam = sam;
		}

		public override string Analyze()
		{
			var text = _sam.Analyze();

			var __dupStrPtn = _duplicateSeparator + "+";

			text = Regex.Replace(text, @__dupStrPtn, string.Empty);

			return text;

		}
	}


	/// <summary>
	/// 데이터를 공백을 모두 제거하고 새로운 구분자로 변경한다
	/// 1. 라인의 시작 공백문자 모두 없앰
	/// 2. 빈라인 모두 없앰
	/// 3. 마지막 라인의 개행 문자 없앰(\n)
	/// 4. 구분자로 쓰인 현재의 문자열(여러개 중복되더라도) 하나의 구분 문자열로 변경한다 (기본값은 현재 구분자는 공백문자, 변경은 탭)
	/// </summary>
	public class NormalizationStringAnalyzerM : AbStringAnalyzerDecoM
	{
		string _changeSeparator; // 바꿀 구분자
		string _curSeparator; // 현재 구분자

		public NormalizationStringAnalyzerM(IStringAnalyzerM sam, string changeSeparator = "\t", string curSeparator = StringAnalyzerM.SPACE_CHARS)
		{
			_sam = sam;
			_changeSeparator = changeSeparator;
			_curSeparator = curSeparator;

		}

		public override string Analyze()
		{
			var text = _sam.Analyze();

			// 1. 하나 이상의 (공백, 탭)문자를 모두 separator 문자열 한개로 대체 (개행하고 일반문자 빼고)
			text = Regex.Replace(text, _curSeparator, _changeSeparator);

			// 2. 각라인의 공백문자로 시작한다면 공백을 모두 제거 (여기서 ^는 처음을 의미)
			text = Regex.Replace(text, @"^\s+", string.Empty, RegexOptions.Multiline);

			// 3. 각라인의 끝의 공백 문자를 모두 제거 (여기서 ^는 부정을 의미)
			text = Regex.Replace(text, @"[^\S\n]+$", string.Empty, RegexOptions.Multiline);

			// 3. 마지막 라인의 개행문자 없앰
			text = text.TrimEnd(new char[] { '\r', '\n' });

			return text;
		}
	}

}
