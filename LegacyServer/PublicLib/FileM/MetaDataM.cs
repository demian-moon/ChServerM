using FbsClassM;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EcsServerLibM
{

	/// <summary>
	/// 모든 메타파일의 정보를 가지고 있고 GetTableRunTime() 함수를 통해서 해당 라인의 데이터 클래스를 만들어냄
	/// MetaDataM 을 가지고 T타입(LoadableDataInStructM)의 클래스 인스턴스를 만들어 내는 매니져
	/// T타입 클래스는 public이어야 하고 변수명들이 모두 MetaDataM의 헤더컬럼의 이름과 같아야 함
	/// </summary>
	/// <typeparam name="T"></typeparam>
	public class MetaDataRuntimeTableMan<T> where T : LoadableDataInStructM, new()
	{
		public MetaDataM MetaData { get; set; }
		ConcurrentDictionary<string, T> _dicRuntimeTable;
		public MetaDataRuntimeTableMan(MetaDataM metaData)
		{
			MetaData = metaData;
			_dicRuntimeTable = new ConcurrentDictionary<string, T>();
		}

		/// <summary>
		/// 메타데이터에서 라인의 key로 해당 값들을 T 타입 클래스로 얻기 (T타입 클래스의 변수명들은 public에 헤더컬럼명하고 같아야함)
		/// </summary>
		/// <param name="strKeyLine"></param>
		/// <returns></returns>
		public T GetTableRunTime(string strKeyLine)
		{
			return _dicRuntimeTable.GetOrAdd(strKeyLine, key => MetaData.GetTableRuntime<T>(key));
		}
	}

    /// <summary>
    /// 메타 데이터 클래스
    /// 1. static MetaDataM GetMetaDataFromExcel()를 통해서 엑셀에 있는 데이터를 가져온다
    /// 2. FbsMetaData 로 MetaDataM 생성
    /// 3. string으로 GetMetaDataFromString()을 통해서 MetaDataM을 생성
    /// 주의 사항 : IndexMeta로 설정할 경우에는 key가 숫자형 문자로 되어 있는 것이 하나라도 있으면 안된다!!!
    /// 참고 : IndexMeta란 기획자는 key를 string으로 사용하면서, 서버 클라 전송간 key를 int로 전달하기 위한 메타
	/// 
    /// </summary>	//
    public class MetaDataM
	{
		Dictionary<string, int> _dicHeaderIdx = new Dictionary<string, int>(); //헤더 Idx는 0부터 시작
		Dictionary<string, string[]> _dicLineStr = new Dictionary<string, string[]>();  // 해당라인의 key에 해당하는 컬럼의 값과, 실제 라인 스트링 배열을 의미하는 string []        

		// 테이블 라인 순서대로 매겨진 index번호(의도하지 않은 오류 피하기 위해 index저장은 string과 index조합으로)와 실제 key를 가지고 있는 딕셔너리
		Dictionary<string, string> _dicIndexToKey;
        Dictionary<string, string> _dicKeyToIndex;

        readonly string INDEX_PRE_STR = "_!@_";

		public int _iCntCol;
		public int _iCntRow;
		int _idxKeyHeader; // 키 헤더 컬럼의 idx 값
		bool _bIndexMeta; // row순으로 매겨지는 index 메타인지 설정 함수
		public bool IsIndexMeta { get => _bIndexMeta; }

		public string StrHeaderKey { get; set; } = string.Empty;   // 헤더 컬럼의 Key값
		public int IdxKeyHeader { get => _idxKeyHeader; } // 헤더 키의 idx값

#if NETFRAMEWORK
        // 엑셀 테이블을 가지고 메타테이블 만들기
        /// <summary>
        /// 
        /// </summary>
        /// <param name="strKeyHeader">헤더 문자열중 키값이 되는 문자열 값</param>
        /// <param name="excelTable"></param>
        public MetaDataM(string strKeyHeader  , ExcelTableM excelTable)
            : this(strKeyHeader, excelTable.GetHeaderString(), excelTable.GetRowStringAll())
        {
        }
#endif
		// 전송할 TransmissionMeta 파일 생성시 사용
		private MetaDataM(string strHeaderKey, Dictionary<string, int> dicHeaderIdx, Dictionary<string, string[]> dicLineStr)
		{
			StrHeaderKey = strHeaderKey;
			_dicHeaderIdx = dicHeaderIdx;
            _dicLineStr = dicLineStr;

            _iCntCol = dicHeaderIdx.Count;  // 컬럼 개수
			_iCntRow = dicLineStr.Count;    // Row개수
			_idxKeyHeader = _dicHeaderIdx[StrHeaderKey];   // 헤더 키의 idx			 
        }

		/// <summary>
		/// 
		/// </summary>
		/// <param name="fbsMetaData">플랫버퍼 클래스 형태의 메타데이터</param>
		public MetaDataM(FbsMetaData fbsMetaData)
		{

			_iCntCol = fbsMetaData.HeaderLength;
			_iCntRow = fbsMetaData.LineLength;
			StrHeaderKey = fbsMetaData.StrKey;

			for (int i = 0; i < _iCntCol; i++)
			{
				string strHeader = fbsMetaData.Header(i);

				if (strHeader.CompareTo(StrHeaderKey) == 0)  // 키하고 같으면 그값이 키의 컬럼 idx값
				{

					_idxKeyHeader = i;
				}

				_dicHeaderIdx[strHeader] = i;
			}

			for (int i = 0; i < _iCntRow; i++)
			{
				string[] lineStr = new string[_iCntCol];
				string dataKeyCol = "";
				for (int k = 0; k < _iCntCol; k++)
				{
					string data = fbsMetaData.Line(i).Value.ArrStr(k);
					lineStr[k] = data;

					if (k == _idxKeyHeader) // 키 컬럼이면 라인 index 값을 dic에 저장
					{
						dataKeyCol = data;
					}
				}

				_dicLineStr[dataKeyCol] = lineStr;
            }            
        }
		
		

		/// <summary>
		/// 
		/// </summary>
		/// <param name="strHeaderKey">헤더 문자열중 키값이 되는 문자열 값</param>
		/// <param name="strHeaderList">헤더의 문자열 리스트</param>
		/// <param name="strLineList">라인 문자열 리스트</param>
		public MetaDataM(string strHeaderKey, IEnumerable<string> strHeaderList, IEnumerable<IEnumerable<string>> strLineList)
		{
			//if (strHeaderList.Count() != strLineList.First().Count())
			//{
			//    throw new FormatException("헤더 컬럼 개수와 라인 컬럼 개수가 다름");
			//}    

			try
			{

				StrHeaderKey = strHeaderKey;

				int i = 0;
				foreach (string strHeader in strHeaderList)
				{
					if (strHeader.CompareTo(StrHeaderKey) == 0)  // 키하고 같으면 그값이 키의 컬럼 idx값
					{
						_idxKeyHeader = i;
					}
					_dicHeaderIdx[strHeader] = i;
					_iCntCol++;

					i++;
				}



				foreach (var tmLine in strLineList)
				{
					string[] lineStr = new string[_iCntCol];
					string dataKeyCol = "";

					int k = 0;
					foreach (string data in tmLine)
					{
						lineStr[k] = data;
						if (k == _idxKeyHeader) // 키 컬럼이면 라인 index 값을 dic에 저장
						{
							dataKeyCol = data;
						}
						k++;
					}

					_dicLineStr[dataKeyCol] = lineStr;
					_iCntRow++;
				}
			}
			catch (Exception ex)
			{
				throw new FormatException($"메타데이터의 헤더와 라인 개수가 맞지 않습니다. 헤더 : {strHeaderKey} , 라인 : {string.Join(",", strLineList)}", ex);
			}



		}

		public MetaDataM(string strHeaderKey, IEnumerable<string> strHeaderList, IEnumerable<string> lines)
			: this(strHeaderKey, strHeaderList, lines.Select(str => str.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries) ) )

		{

		}

		/// <summary>
		/// 서버에서 클라이언트에 메타를 전송하면 클라이언트는 메타를 받아서 이 함수로 인덱스 메타로 변환해야 한다.
		/// 왜냐하면 메타 전송후 클라에서는 메타의 키값을 인덱스로 보내기 때문이다.
		/// 주의 !! : 클라로 메타를 보낼 때 이 함수를 사용 해서 인덱스 메타로 컨버트 한 후에 보내면 안된다. 
		/// 그럼 클라에서 사용할 때 key값이 인덱스가 되어 버린다.
		/// </summary>
		public void ConvertToIndexMeta()
        {
			if(_bIndexMeta == true) // 이미 인덱스 메타임
				return;

            _bIndexMeta = true;
            // 인덱스 메타 변환 - 키 값을 row순으로 index로 변경함
            _dicIndexToKey = new Dictionary<string, string>();
            _dicKeyToIndex = new Dictionary<string, string>();

            var newDicLineStr = new Dictionary<string, string[]>();

            int k = 0;
            foreach (var pair in _dicLineStr)
            {
                string strIdx = k.ToString();
                _dicIndexToKey.Add(strIdx, pair.Key);
                _dicKeyToIndex.Add(pair.Key, strIdx);

                pair.Value[_idxKeyHeader] = strIdx;
                newDicLineStr.Add(strIdx, pair.Value);

                k++;
            }
            _dicLineStr = newDicLineStr; // 인덱스 변환												
        }

		/// <summary>
		/// indexKey로 메타 라인키를 찾는다
		/// </summary>
		/// <param name="indexKey">index 메타의 key</param>
		/// <param name="lineKey">라인의 key값</param>
		/// <returns></returns>
		public bool TryGetLineKeyWithIndexKey(string indexKey, out string lineKey)
		{
			if (_bIndexMeta == false)
			{
				throw new InvalidOperationException("메타 테이블이 Index Meta가 아닙니다.");
			}

			return _dicIndexToKey.TryGetValue(indexKey, out lineKey);
		}

		public bool TryGetIndexKeyWithLineKey(string lineKey, out string indexKey)
		{
			if (_bIndexMeta == false)
			{
				throw new InvalidOperationException("메타 테이블이 Index Meta가 아닙니다.");
			}

			return _dicKeyToIndex.TryGetValue(lineKey, out indexKey);
			
		}


		/// <summary>
		/// 특정 컬럼값(다른 테이블의 uniqueKey)을 다른 테이블에서 찾아서 rowIndex로 바꾼다
		/// </summary>
		/// <param name="convertColHeader">변환할 컬럼의 헤더 문자열</param>
		/// <param name="refIndexMetaM">인덱스 메타여야 한다</param>
		/// <returns></returns>
		/// <exception cref="ArgumentException"></exception>
		public void ConvertColToIndexRefMetaM(string convertColHeader, MetaDataM refIndexMetaM)
		{
			if (_dicHeaderIdx.TryGetValue(convertColHeader, out var colIdx))
			{
				ConvertColToIndexRefMetaM(colIdx, refIndexMetaM);
			}
			else
			{
				throw new ArgumentException($"{convertColHeader} does not exist.");
				//ServerM.logM.Warn($"{targetColHeader} could not be found.");
			}
		}

		/// <summary>
		/// 특정 컬럼값이 다른 테이블의 key값을 갖는 경우 해당 컬럼값을 index값으로 변경한다
		/// !기획자가 작업할 때의 테이블은 테이블의 key값으로 작업하고 실제 메모리에는 index값으로 찾기 위해
		/// </summary>
		/// <param name="refMetaKeyCol">변환할 헤더 인덱스</param>
		/// <param name="refIndexMetaM">인덱스 메타여야 한다</param>
		/// <exception cref="ArgumentException"></exception>
		public void ConvertColToIndexRefMetaM(int convertColHeaderIdx, MetaDataM refIndexMetaM)
		{
			if(refIndexMetaM.IsIndexMeta == false)
			{
				throw new ArgumentException($"참조하는 메타가 IndexMeta가 아닌 메타입니다.");
			}
						
			foreach(var lineKey  in _dicLineStr.Keys)
			{
				_dicLineStr.TryGetValue(lineKey, out var lineArray);
				var refTableKey = lineArray[convertColHeaderIdx];

				refIndexMetaM._dicKeyToIndex.TryGetValue(refTableKey, out var rowIndex); // 해당 key의 rowIndex 구하기								
				lineArray[convertColHeaderIdx] = rowIndex;	// 레퍼런스 테이블의 index 값으로 변경해서 메모리에서 들고 있기
			}
		}
		
		/// <summary>
		///  row 순으로 매겨지는 인덱스 컬럼을 추가한다
		/// </summary>
		/// <param name="strUniqueKey">null이면 기존 유니크키 컬럼을 유지한다</param>
		/// <param name="strIndexHeaderKey"></param>
		/// <param name="insertCol"></param>
		/// <returns></returns>
		/// <exception cref="ArgumentException"></exception>
		public MetaDataM InsertIndexCol(string strUniqueKey, string strIndexHeaderKey, int insertCol)
		{
			if(DataExist(strIndexHeaderKey) == true)
			{
				throw new ArgumentException($"{strIndexHeaderKey} already exist!!");
			}

			var headerList = GetHeaderList();
			var lineAll = GetLineListAll();

			var newHeaderList = headerList.ToList();
			newHeaderList.Insert(insertCol, strIndexHeaderKey);

			//StringBuilder sb = new StringBuilder();
			int keyIndex = 0;
			var newLineAll = new List<List<string>>();
			foreach(var line in lineAll)
			{
				var newLine = line.ToList();
				newLine.Insert(insertCol, keyIndex.ToString());
				newLineAll.Add(newLine);
				keyIndex++;
			}
						
			if (string.IsNullOrEmpty(strUniqueKey))
				strUniqueKey = StrHeaderKey;

			return new MetaDataM(strUniqueKey, newHeaderList, newLineAll);
		}

#if NETFRAMEWORK
        /// <summary>
        /// 스태틱 함수 - 메타파일 MetaDataM 얻기 (Header는 헤더마크로 시작해야 함)
        /// </summary>
        /// <param name="efm"></param>
        /// <param name="sheetName"></param>
        /// <param name="strKeyHeader"></param>
        /// <returns></returns>
        static public MetaDataM GetMetaDataFromExcel(ExcelFileM efm, string sheetName, string strKeyHeader)
        {
            ExcelTableM mapExcelTable = efm.GetExcelTable(sheetName, strKeyHeader);
            return new MetaDataM(strKeyHeader, mapExcelTable);
        }
#endif
		static public async Task<MetaDataM> GetMetaDataFromFileAsync(string filePath)
		{
			if (File.Exists(filePath) == false)
				throw new FileNotFoundException($"파일 없음 : {filePath}");

			var metaStr = await FileM.ReadStringAsync(filePath).ConfigureAwait(false);

			return GetMetaDataFromString(metaStr);

			// 1. 하나 이상의 (공백, 탭)문자를 모두 탭으로 (개행하고 일반문자 빼고)
			//pureText = Regex.Replace(pureText, @"[^\S\n]+", "\t");


			// 2. 각라인의 공백문자로 시작한다면 공백을 모두 제거
			//pureText = Regex.Replace(pureText, @"^\s+", string.Empty, RegexOptions.Multiline);

			//if (pureText.StartsWith(headMark) == false)
			//{
			//    throw new FormatException($"메타파일의 헤더는 {headMark} 로 시작해야 합니다.");
			//}

			//pureText = pureText.Substring(headMark.Length); // 헤더마크 지우기

			// 3. 헤더마크 지운후 시작을 공백 문자로 시작한다면 공백을 모두 제거
			//pureText = Regex.Replace(pureText, @"^\s+", string.Empty, RegexOptions.Singleline);
		}

		static public MetaDataM GetMetaDataFromFile(string filePath)
		{
			if (File.Exists(filePath) == false)
				throw new FileNotFoundException($"파일 없음 : {filePath}");

			var metaStr = FileM.ReadString(filePath);

			return GetMetaDataFromString(metaStr);

			// 1. 하나 이상의 (공백, 탭)문자를 모두 탭으로 (개행하고 일반문자 빼고)
			//pureText = Regex.Replace(pureText, @"[^\S\n]+", "\t");


			// 2. 각라인의 공백문자로 시작한다면 공백을 모두 제거
			//pureText = Regex.Replace(pureText, @"^\s+", string.Empty, RegexOptions.Multiline);

			//if (pureText.StartsWith(headMark) == false)
			//{
			//    throw new FormatException($"메타파일의 헤더는 {headMark} 로 시작해야 합니다.");
			//}

			//pureText = pureText.Substring(headMark.Length); // 헤더마크 지우기

			// 3. 헤더마크 지운후 시작을 공백 문자로 시작한다면 공백을 모두 제거
			//pureText = Regex.Replace(pureText, @"^\s+", string.Empty, RegexOptions.Singleline);
		}

		/// <summary>
		/// 맨 첫라인이 헤더라인이 되는 메타파일 
		/// </summary>
		/// <param name="metaStr"></param>
		/// <returns></returns>
		/// <exception cref="FormatException"></exception>
		static public MetaDataM GetMetaDataFromString(string metaStr)
		{
			StringAnalyzerM fa = new StringAnalyzerM(metaStr);
			IStringAnalyzerM ifa = new CommentStringAnalyzerM(fa);    // 코멘트 모두 없애기
			ifa = new NormalizationStringAnalyzerM(ifa);

			string pureText = ifa.Analyze();

			using StringReader sr = new StringReader(pureText); // using 리소스 해제
																// 헤더와 데이터 분리
			string headerText = sr.ReadLine();
			string contentsText = sr.ReadToEnd();
			if (headerText == null)
			{
				throw new FormatException($"메타파일의 헤더 데이터가 없습니다\n첫라인이 헤더 입니다.");
			}

			if (string.IsNullOrEmpty(contentsText))
			{
				throw new FormatException($"메타파일의 데이터가 없습니다");
			}

			string[] headerStrings = headerText.Split(new char[] { '\t' });
			string[] textLines = contentsText.Split(new char[] { '\n' });

			var rtnMetaDataM = new MetaDataM(headerStrings[0], headerStrings, textLines);

			return rtnMetaDataM;
		}

		public bool TryGetHeaderIdx(string strHeader, out int idxHeader)
		{
			return _dicHeaderIdx.TryGetValue(strHeader, out idxHeader);
		}

		public IEnumerable<string> GetHeaderList()
		{
			return _dicHeaderIdx.Keys;
		}

		/// <summary>
		/// 각 라인들의 키값 리스트를 얻어온다
		/// </summary>
		/// <returns></returns>
		public List<string> GetLineKeyList()
		{
			List<string> strKeyList = new List<string>();

			foreach (var strLine in GetLineListAll())
			{
				strKeyList.Add(strLine.ElementAt(_idxKeyHeader));
			}

			return strKeyList;
		}

		/// <summary>
		/// 각 라인의 스트링 리스트를 모두 얻어온다
		/// </summary>
		/// <returns></returns>
		public IEnumerable<IEnumerable<string>> GetLineListAll()
		{
			return _dicLineStr.Values;
		}

		/// <summary>
		/// 전체 메타 데이터를 구분자로 구분해 string으로 만든다
		/// </summary>
		/// <param name="separator"></param>
		/// <returns></returns>
		public string GetString(string separator = "\t")
		{
			var sb = new StringBuilder();
			int __iCol = 0;
			foreach (var header in GetHeaderList())
			{
				__iCol++;
				sb.Append(header);
				if (__iCol != _iCntCol)
				{
					sb.Append(separator);
				}
			}
			sb.Append('\n');


			int __iRow = 0;
			foreach (var line in GetLineListAll())
			{
				__iRow++;
				__iCol = 0;
				foreach (var str in line)
				{
					__iCol++;
					sb.Append(str);
					if (__iCol != _iCntCol)
					{
						sb.Append(separator);
					}
				}

				if (__iRow != _iCntRow)
				{
					sb.Append("\n");
				}
			}

			return sb.ToString();
		}

		public string[] GetStrLine(string strKeyLine)
		{
			_dicLineStr.TryGetValue(strKeyLine, out string[] strLine);
			return strLine;

		}


		public int GetDataInteger(string strKeyLine, string strHeader)
		{
			var __data = GetData(strKeyLine, strHeader);
			if (string.IsNullOrEmpty(__data))
			{
				Debug.Assert(false, $"{strKeyLine}의 {strHeader} 컬럼값이 null");
			}

			return int.Parse(__data);
		}

		/// <summary>
		/// 라인의 키값값과, header 문자열(컬럼)을 가지고 string 얻기
		/// </summary>
		/// <param name="strKeyLine"></param>
		/// <param name="strHeader"></param>
		/// <returns></returns>
		public string GetData(string strKeyLine, string strHeader)
		{
			if (string.IsNullOrEmpty(strKeyLine) || string.IsNullOrEmpty(strHeader))
				return "";

			_dicHeaderIdx.TryGetValue(strHeader, out int idxHeader);

			return GetData(strKeyLine, idxHeader);
		}

		public int GetDataInteger(string strKeyLine, int idxHeader)
		{
			var __data = GetData(strKeyLine, idxHeader);
			if (string.IsNullOrEmpty(__data))
			{
				Debug.Assert(false, $"{strKeyLine}의 {idxHeader} 컬럼값이 null");
			}

			return int.Parse(__data);
		}

		/// <summary>
		/// 라인의 키컬럼에 해당하는 값과, index를 가지고 string 얻기
		/// index 0은 key와 상관없이 해당라인의 첫번째 값임을 주의!!!!
		/// </summary>
		/// <param name="strKeyLine"></param>
		/// <param name="idxHeader">index 0번이 제일 첫번째 컬럼(보통 key)</param>
		/// <returns></returns>
		public string GetData(string strKeyLine, int idxHeader) //
		{
			if (idxHeader >= _iCntCol || idxHeader < 0)
			{
                //throw new ArgumentOutOfRangeException("idxHeader", $"idxHeader값 {idxHeader} 이 fbs headerLength {_iColLen} 값과 같거나 큼");
                throw new ArgumentException($"idxHeader값 {idxHeader} 이 fbs headerLength {_iCntCol} 값과 같거나 큼");				
			}

			if (_dicLineStr.TryGetValue(strKeyLine, out string[] lineStr) == false)
			{
				if (_bIndexMeta == true) // 인덱스 메타일 경우 메모리상에 메타테이블의 key값들이 Index로 변경되어 있으므로 key를 index로 변경해서 조회 해야 함
				{
					if (_dicKeyToIndex.TryGetValue(strKeyLine, out var findIndexKey))
					{
						_dicLineStr.TryGetValue(findIndexKey, out lineStr);// strKeyLine이 index일 수 있음
					}
					else
					{
						throw new ArgumentException($"{strKeyLine} 테이블 값이 없음 - indexMeta임");
					}

					
				}
				else
				{
					throw new ArgumentException($"{strKeyLine} 테이블 값이 없음");
				}
			}

			return lineStr[idxHeader];
		}

		/// <summary>
		/// 헤어 컬럼에 해당하는 데이터를 모두 IEnumerable<string> 으로 리턴
		/// </summary>
		/// <param name="strHeader"></param>
		/// <returns></returns>
		public IEnumerable<string> GetDataCol(string strHeader)
		{
			_dicHeaderIdx.TryGetValue(strHeader, out int idxHeader);
			return GetDataCol(idxHeader);
		}

		/// <summary>
		/// idxHeader값 0은 키와 상관없이 각 라인의 첫번째 데이터(즉 첫번째 값이 키라도 키를 리턴함)
		/// </summary>
		/// <param name="idxHeader"></param>
		/// <returns></returns>
		public IEnumerable<string> GetDataCol(int idxHeader)
		{
			return GetLineListAll().Select(line => line.ElementAt(idxHeader));
		}

		public bool DataExist(string strKeyLine)
		{
			if (_dicLineStr.TryGetValue(strKeyLine, out string[] _) == true)
				return true;

			if(_bIndexMeta == true)
			{
				_dicIndexToKey.TryGetValue(strKeyLine, out var findKey);
				if(_dicLineStr.TryGetValue(findKey, out var _) )
					return true;					
			}
			return false;
		}

        // 
        /// <summary>
        /// 선별할 헤더키 문자열 리스트를 받고 메타데이터 클래스를 리턴 
        /// </summary>
        /// <param name="selHeaderKeyList"></param>
        /// <param name="bConvertLineKeyToIdx">true이면 라인키를 index로 변경함</param>
        /// <returns></returns>
        public MetaDataM GetTransmissionMetaData(IEnumerable<string> selHeaderKeyList, bool bConvertLineKeyToIdx)
		{

			List<string> selectHeaderKeyList = selHeaderKeyList.ToList();

			// 테이블 키값이 빠져 있다면 자동 추가
			if (selectHeaderKeyList.Contains(StrHeaderKey) == false)
			{
				selectHeaderKeyList.Add(StrHeaderKey);
			}

			// 해당 dicHeaderIdx의 value 즉 index만 뽑아 배열로
			int[] selectHeaderKeyIdxes = _dicHeaderIdx.Where(pair => selectHeaderKeyList.Contains(pair.Key)).Select(pair => pair.Value).ToArray();

			// 해당 key리스트들만 dicHeaderIdx에서 선택해서 dic으로 
			Dictionary<string, int> tmDicHeaderIdx = _dicHeaderIdx.Where(pair => selectHeaderKeyList.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
			
			int k = 0;
			var tmDicLineStr = _dicLineStr.ToDictionary(pair => pair.Key, pair =>
				selectHeaderKeyIdxes.Select(index =>
                {
                    //if (bConvertLineKeyToIdx && _idxKeyHeader == index) // 인덱스 메타로 전송하는 것이면
                    //{
                    //    return k++.ToString();
                    //}
                    return pair.Value[index]; 
				}).ToArray());
		
			var transMeta = new MetaDataM(StrHeaderKey, tmDicHeaderIdx, tmDicLineStr);
			if(bConvertLineKeyToIdx)
				transMeta.ConvertToIndexMeta(); // 인덱스 메타로 변환해서 리턴

			return transMeta;

		}


        /// <summary>
        /// 선별할 헤더 키 index 리스트를 받고 메타데이터 클래스를 리턴
        /// </summary>
        /// <param name="selIdxHeaderKeyList"></param>
        /// <param name="bConvertLineKeyToIdx">true이면 라인키를 index로 변경함 - 클라에서 의미있는 key값을 가지고 참조 필요가 없으니 key자체를 숫자로 변경해서 데이터를 줄임</param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public MetaDataM GetTransmissionMetaData(IEnumerable<int> selIdxHeaderKeyList, bool bConvertLineKeyToIdx)
		{
			if (selIdxHeaderKeyList.Any(idx => idx < 0 && idx >= _iCntCol))
			{
				Debug.WriteLine($" index 값중에 마이너스 또는 컬럼수({_iCntCol})보다 크거나 같음");
				throw new ArgumentException();
			}

			List<int> selectIdxHeaderKeyList = selIdxHeaderKeyList.ToList();

			// 테이블 키값이 빠져 있다면 자동 추가
			if (selectIdxHeaderKeyList.Contains(_idxKeyHeader) == false)
			{
				selectIdxHeaderKeyList.Add(_idxKeyHeader);
			}

			var selectHeaderKeyList = _dicHeaderIdx.Where(pair => selectIdxHeaderKeyList.Contains(pair.Value)).Select(pair => pair.Key).ToArray();
			return GetTransmissionMetaData(selectHeaderKeyList, bConvertLineKeyToIdx);
		}

		// 라인키값으로 LoadableDataInStructM 상속받은 클래스를 생성해서 리턴
		public T GetTableRuntime<T>(string strKeyLine) where T : LoadableDataInStructM, new()
		{
			var rtnRuntimeTable = new T();
			foreach (var varName in _dicHeaderIdx.Keys)
			{
				var data = GetData(strKeyLine, varName);
				rtnRuntimeTable.SetData(varName, data);
			}
			return rtnRuntimeTable;
		}
	}

}
