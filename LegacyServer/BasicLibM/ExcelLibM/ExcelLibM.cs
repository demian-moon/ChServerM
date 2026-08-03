//using Microsoft.Office.Interop.Excel;
//using System.Runtime.InteropServices;
//using Range = Microsoft.Office.Interop.Excel.Range;

namespace EcsServerLibM
{
#if NETFRAMEWORK

	public class ExcelTableM
	{

		/// <summary>
		/// 순수 데이터 만 있는 클래스 index 0부터 시작함, _dicHeader또한 순수 데이터 : 코멘트 없음
		/// </summary>
        private class ExcelRowM
        {
            int _iCntColumn;
            Dictionary<string, int> _dicHeader;
			
            object[] _data;	// 코멘트 없는 순수한 데이터

            public ExcelRowM(Dictionary<string, int> dicHeader, object[] data)
            {
                _dicHeader = dicHeader;
                _data = data;
                _iCntColumn = dicHeader.Count;
            }

            public object GetData(string key)
            {                
				if(_dicHeader.TryGetValue(key, out int idx) == true)
					return _data[idx];

				return null;
            }

            public object GetData(int idx)	// index 0 부터
            {
                return _data[idx];
            }

        }

        Dictionary<string, int> _dicHeader = new Dictionary<string, int>();		// 코멘트 없는 순수한 헤더 데이터의 idx번호 0부터 시작
		Dictionary<string, ExcelRowM> _dicErows = new Dictionary<string, ExcelRowM>();	// 헤더키 idx에 해당하는 라인별 키값, 그라인 정보
		int _idxTableKeyColExcel; // 키가 위치하는 idx 컬럼  : 1부터시작 엑셀 Range상의 키가 되는 컬럼 idx (중간 코멘트 컬럼 있는것과 상관없는 실제 excel의 idx)
		public string StrTableKeyColExcel { get; set; } = string.Empty; // 엑셀 헤더 상의 키가되는 컬럼 문자열

        string[] _arrStrHeaders;    // 헤더컬럼들의 문자열 
        Dictionary<int, string> _dicCommentHeaderIdx;    // 코멘트가 있는 엑셀 헤더 idx 1부터 시작 (배열 인덱스 값임)

        Dictionary<int, string> _dicCommentHeaderIdxInExcel; // 실제 엑셀파일에서 코멘트의 컬럼 idx 

		int _startRowHeader;
		int _startColHeader;

        ExcelRowM[] _eRows;	// ExcelRow의 배열 index 0부터 시작함 (헤더는 없음)
		public int CntColumn { get { return _iCntColumn; } }	// 코멘트를 제외한 실제 컬럼개수
		public int CntRow { get { return _iCntRow; } }     // 코멘트를 제외한 실제 Row개수

		int _iCntColumn;
		int _iCntRow;

		object[,] _rawData; // index는 1부터 시작이니까 주의
		
		public ExcelTableM(Range rngTable, string strCommentStart, string strKey) // strKey 키로 쓸 컬럼 텍스트
        {
            _startRowHeader = rngTable.Row;
            _startColHeader = rngTable.Column;

            _rawData = (object[,])rngTable.Value;
			var rngKey = rngTable.Find(strKey);

			if (rngKey == null)
			{
				Debug.WriteLine("버그 key컬럼이 없음:" + strKey);
				return;
			}

			StrTableKeyColExcel = strKey;
            _idxTableKeyColExcel = rngKey.Column - rngTable.Column + 1;
			Parse(strCommentStart);
        }

		public string[] GetHeaderString()	// 순수 데이터 헤더만 리턴함
		{
			return _arrStrHeaders;
		}

		public string[][] GetRowStringAll()
		{
			object[][] allObject = GetRowObjectAll();
			return allObject.Select(row => row.Select(ConvertToStr).ToArray()).ToArray();			
        }

		static string ConvertToStr(object obj)
		{
			string rtnStr = "";
			if (obj == null)
				return rtnStr;

			if(obj is double)
			{
				return rtnStr = ((double)obj).ToString();
			}
			else
			{
				rtnStr = obj.ToString();
			}

			return rtnStr;
		}

		public object[][] GetRowObjectAll()
		{
			object[][] rtnObjAll = new object[_iCntRow][];

			for(int i = 0; i < _iCntRow; i++)
			{
				rtnObjAll[i] = GetRowObject(i);
			}
			return rtnObjAll;
		}

		public object[] GetRowObject(int iRow)	// 순수 데이터만 리턴함 (데이터가 object 형태로 저장되어 있음
		{

			if(iRow >= _iCntRow)
			{
				return new object[0];
			}

			var rtnArrStr = new object[_iCntColumn];
			for(int iCol = 0; iCol < _iCntColumn; iCol++)
			{
                rtnArrStr[iCol] = _eRows[iRow].GetData(iCol);
			}

			return rtnArrStr;
        }

		public Dictionary<int, string> GetDicCommentHeaderIdxInExcel()
		{
			if (_dicCommentHeaderIdx == null)
				return null;

			return _dicCommentHeaderIdxInExcel;
		}

		public object GetData(string key, string column)
		{
			_dicErows.TryGetValue(key, out ExcelRowM erow);
			return erow?.GetData(column);
		}

		public T[] ReadTable<T>() where T : LoadableDataInStructM, new ()
		{
			T checkTypeT = new T();
			if(LoadableDataInStructM.CheckCorrectDataField(checkTypeT.GetType(), _arrStrHeaders) == false)
			{
				Debug.WriteLine($"{checkTypeT.GetType()}이 문제야 헤더가 안맞음");
				throw new TypeLoadException($"{checkTypeT.GetType()} 타입중 테이블 헤더와 같은 이름의 멤버 변수가 없음"); // 타입이 이상함
			}

            T[] table = new T[_iCntRow];
			for(int iRow = 0; iRow < _iCntRow; iRow++)
			{
				T rowClass = new T();
				for(int iCol = 0; iCol < _iCntColumn; iCol++)
				{
					var keyColumn = _arrStrHeaders[iCol];
					rowClass.SetData(keyColumn, _eRows[iRow].GetData(iCol));
				}

				table[iRow] = rowClass;
			}

			return table;
		}

		public T ReadTableHeader<T>() where T : LoadableDataInStructM, new ()
		{
            T checkTypeT = new T();
            if (LoadableDataInStructM.CheckCorrectDataField(checkTypeT.GetType(), _arrStrHeaders) == false)
            {
                Debug.WriteLine($"{checkTypeT.GetType()}이 문제야 헤더가 안맞음2");
                throw new TypeLoadException($"{checkTypeT.GetType()} 타입중 테이블 헤더와 같은 이름의 멤버 변수가 없음2"); // 타입이 이상함
            }

            T header = new T();
			
            for (int iCol = 0; iCol < _iCntColumn; iCol++)
            {
                var keyColumn = _arrStrHeaders[iCol];
                header.SetData(keyColumn, keyColumn);
            }
			return header;
        }


		public void ParseHeader(string strCommentStart)
		{			
            _dicCommentHeaderIdx = new Dictionary<int, string>();
			_dicCommentHeaderIdxInExcel = new Dictionary<int, string>();
            List<string> headerList = new List<string>();

            // 헤더 파싱
            int iLenColumn = _rawData.GetLength(1);  // 컬럼 길이
            string strCell;
            int idxHeaderCol = 0;
            for (int iCol = 1; iCol <= iLenColumn; iCol++)
            {
                strCell = _rawData[1, iCol]?.ToString();    // null일 수 있음 - 컬럼헤더가
                if (strCell != null)
                {
                    strCell = strCell.Trim();
                    if (strCell.StartsWith(strCommentStart) == false)
                    {
                        _dicHeader[strCell] = idxHeaderCol++;
                        headerList.Add(strCell);
                    }
                    else
                    {
                        _dicCommentHeaderIdx.Add(iCol, strCell);
                        _dicCommentHeaderIdxInExcel.Add(_startColHeader + iCol - 1, strCell);
                    }
                }
                else
                {
                    _dicCommentHeaderIdx.Add(iCol, string.Empty);
                    _dicCommentHeaderIdxInExcel.Add(_startColHeader + iCol - 1, strCell);
                }
            }
            _iCntColumn = _dicHeader.Count;
            _arrStrHeaders = headerList.ToArray();
        }


		// 컬럼헤더가 #으로 시작하거나, row key값이 #으로 시작 또는 null일 경우 해당 라인은 제외 함
		private void Parse(string strCommentStart)
		{			
            ParseHeader(strCommentStart); // 헤더 파싱
			ParseRows(strCommentStart);
        }

        private void ParseRows(string strCommentStart)
		{
            string strKey;
            int iLenColumn = _rawData.GetLength(1);  // 컬럼 길이

            // 로우 길이 파싱
            int iLenRow = _rawData.GetLength(0); // 로우 길이

            List<ExcelRowM> listErow = new List<ExcelRowM>();
            for (int iRow = 2; iRow <= iLenRow; iRow++) // 1라인은 헤더
            {
                int idxCol = 0;
                strKey = _rawData[iRow, _idxTableKeyColExcel]?.ToString();		// 헤더키 idx에 해당하는 각 라인별 키값 얻기

                if (strKey != null)
                {
                    strKey = strKey.Trim();
                    if (strKey.StartsWith(strCommentStart) == false)
                    {
                        object[] rowData = new object[_iCntColumn];
                        ExcelRowM eRow = new ExcelRowM(_dicHeader, rowData);
                        listErow.Add(eRow);

						if (_dicErows.ContainsKey(strKey) == false)
							_dicErows[strKey] = eRow;
						else
						{
							string errorMsg = "엑셀 키컬럼 값이 중복이 있음:" + strKey;
                            Debug.WriteLine(errorMsg);
							throw new Exception(errorMsg);
						}

                        for (int iCol = 1; iCol <= iLenColumn; iCol++)
                        {
                            if (_dicCommentHeaderIdx.ContainsKey(iCol) == true)              // comment 컬럼은 건너 뛰기			
                                continue;

                            rowData[idxCol++] = _rawData[iRow, iCol];
                        }
                    }
                }
            }

            _iCntRow = _dicErows.Count;
            _eRows = listErow.ToArray();
        }
		        
    }

	public class ExcelRangeM
	{
		Worksheet _ws;
		Range _rng;

		// ws상의 글로벌 좌표 임
		int _startRow = -1;
		int _startCol = -1;
		int _endRow = -1;
		int _endCol = -1;

		public ExcelRangeM(Range rng)
		{
			_ws = rng.Worksheet;
			_rng = rng;
		}

		// ws상의 글로벌 좌표 임
		public int StartRow { get { return _startRow == -1 ? _startRow = _rng.Row : _startRow; } }
		public int StartCol { get { return _startCol == -1 ? _startCol = _rng.Column : _startCol; } }
		public int EndRow { get { return _endRow == -1 ? _endRow = StartRow + _rng.Rows.Count - 1 : _endRow; } }
		public int EndCol { get { return _endCol == -1 ? _endCol = StartCol + _rng.Columns.Count - 1 : _endCol; } }


		public void WriteData<T>(T[] data, string[] arrHeaderStr, Dictionary<int, string> _dicCommentIdx) where T : LoadableDataInStructM
		{
			// 세로 가로, 영역과 데이터 개수 같은지 검사
			if (data.GetLength(0) != _rng.Rows.Count)
			{
				Debug.WriteLine("버그M: WriteData - Row개수가 틀려:" + data.GetLength(0) + ":" + _rng.Rows.Count);
				return;
			}

			FieldInfo fi;
			//int idxRow = 0; 
			int idxColComment = 0;
			int idxRow = 1;
			int idxCol = 1;

			int incCol = StartCol;      // 코멘트 컬럼 증가 처리 위해서 

			Type typeT = typeof(T);
			for (int i = 0; i < data.GetLength(0); i++) // row
			{
				for (int k = 0; k < arrHeaderStr.Length; k++)   // col
				{

					//fi = typeT.GetField(arrHeaderStr[k]); // 값이 없으면 안한당
					//if (fi == null)
					//	continue;

					if (data[i].GetData(arrHeaderStr[k], out object oVal) == false)
					{
						idxCol++;
						continue;
					}

					Range cell = (Range)_rng.Cells[idxRow, idxCol];

					cell.Value = oVal;
					if (oVal?.GetType() == typeof(DateTime))
						cell.NumberFormat = "yyyy/mm/dd hh:mm:ss";

					//idxRow = StartRow + i;
					idxColComment = incCol + k;

					if (_dicCommentIdx.ContainsKey(idxColComment)) // 코멘트 컬럼이면 건너뛰기
					{
						incCol++; // 컬럼 주소 하나 증가 시킴
						continue;
					}

					idxCol++;
					//_rng.Cells[1, 1].Value = "젠트리";
				}
				idxCol = 1; // 초기화 
				idxRow++;
			}


		}


		// 특정 컬럼이 동일한 값들의 묶음으로 range를 분리하기
		public Range[] SplitRangeSameValue<T>(int idxCol, Comparison<T> compFunc = null) where T : struct
		{
			
			List<Range> rtnRng = new List<Range>();
			
			int iColCount = _rng.Columns.Count;			
			int iRowCount = _rng.Rows.Count;


            int iCntSameRng = 1;
            Range rngSameValue = (Range)_rng.Rows[1];

            // row가 1개면
            if (iRowCount <= 1)
			{
				rtnRng.Add(rngSameValue);
				return rtnRng.ToArray();
			}

            T dt1, dt2;
            for (int i=2; i <= iRowCount; i++)
			{	
                
				dt1 = (T)(_rng.Item[i - 1, idxCol]);
				dt2 = (T)(_rng.Item[i, idxCol]);		

                if (compFunc(dt1, dt2) == 0)
				{
					rngSameValue = rngSameValue.Resize[rngSameValue.Rows.Count + 1, iColCount];					
				}
				else
				{
					rtnRng.Add(rngSameValue);
					rngSameValue = (Range)_rng.Rows[i];
                }				
            }

			if(iCntSameRng > 0)
			{
				rtnRng.Add(rngSameValue);
			}

			return rtnRng.ToArray();
		}

#if NETFRAMEWORK
		static public void SetColorSprite(Color color1, Color color2, Range[] arrRng)
		{
			Color setColor = Color.Empty;
			for(int i=0; i < arrRng.Length; i++)
			{
				var rng = arrRng[i];

				if (i % 2 == 0)
					setColor = color1;
				else
					setColor = color2;

				rng.Interior.Color = setColor;

            }
		}
		public void SetColorSprite(Color color1, Color color2)
		{

			Range rngRow;			
			for (int i = 1; i <= _rng.Rows.Count; i++)
			{
				rngRow = (Range)_rng.Rows[i];
				Color setColor;

				if (i % 2 == 1)
					setColor = color1;
				else
					setColor = color2;

				rngRow.Interior.Color = setColor;
			}			
		}

		public void SetRowDesign(int idxRow, Color color, XlLineStyle lineStyle = XlLineStyle.xlLineStyleNone, XlHAlign hAlign = XlHAlign.xlHAlignGeneral)
		{
			if(idxRow < 1 || idxRow > _rng.Rows.Count)
			{
				Debug.Write("idxRow 벗어남" + idxRow);
				return;
			}

			Range rngRow= (Range)_rng.Rows[idxRow];
			
			if(color != Color.Empty)
				rngRow.Interior.Color = color;

			if(lineStyle != XlLineStyle.xlLineStyleNone)
				rngRow.Borders.LineStyle = lineStyle;

			if(hAlign != XlHAlign.xlHAlignGeneral)
				rngRow.HorizontalAlignment = hAlign;
        }

        public void SetColDesign(int idxCol, Color color, XlLineStyle lineStyle = XlLineStyle.xlLineStyleNone, XlHAlign hAlign = XlHAlign.xlHAlignGeneral)
        {
            if (idxCol < 1 || idxCol > _rng.Columns.Count)
            {
                Debug.Write("idxCol 벗어남" + idxCol);
                return;
            }

            Range rngCol = (Range)_rng.Columns[idxCol];

            if (color != Color.Empty)
                rngCol.Interior.Color = color;

            if (lineStyle != XlLineStyle.xlLineStyleNone)
                rngCol.Borders.LineStyle = lineStyle;

            if (hAlign != XlHAlign.xlHAlignGeneral)
                rngCol.HorizontalAlignment = hAlign;
        }

#endif

        // 특정 위치 부터(ws상의 글로벌 좌표) 현재 Range의 마지막 테이블 위치까지
        public Range GetRange(int startRow, int startCol)
		{
			if(startRow < 1 || startCol < 1)
			{
				throw new ArgumentOutOfRangeException();
			}

			if(startRow > EndRow || startCol > EndCol)
			{
				throw new ArgumentOutOfRangeException();
			}

			Range startCell = (Range)_ws.Cells[startRow, startCol]; 
			Range endCell = (Range)_ws.Cells[EndRow, EndCol];

			return _ws.Range[startCell, endCell];
		}

        // 엑셀 형식의 주소 글로벌 문자열로 Range 얻기
        static public Range GetRange(Worksheet ws, string strExcelRowCol)	// 엑셀형식의 주소 문자열 a1:b4
		{
			return ws.Range[strExcelRowCol];
		}

		public enum EXCEL_RANGEM {HEADER_STRAT_CELL, HEADER_END_CELL, HEADER, WITHOUT_HEADER}
		public Range GetRange(EXCEL_RANGEM eRange)
		{

			Range range = null;
			switch (eRange)
			{
				case EXCEL_RANGEM.HEADER:
					range = (Range)_rng.Cells[_rng.Cells[1, 1], _rng.Cells[1, _rng.Columns.Count]];
					break;

				case EXCEL_RANGEM.HEADER_END_CELL:
					range = (Range)_rng.Cells[1, _rng.Columns.Count];
					break;
				case EXCEL_RANGEM.HEADER_STRAT_CELL:
					range = (Range)_rng.Cells[1, 1];
					break;
				case EXCEL_RANGEM.WITHOUT_HEADER:
					if (_rng.Rows.Count <= 1)	// 헤더row만 있고 데이터 가 없으면 null
						return null;
					else
						range = GetRange(StartRow + 1, StartCol); // 헤더라인 제외하고 
					break;
			}
			
			return range;
		}

        static public Range GetRange(Worksheet ws, int sRow, int sCol, int eRow, int eCol)
        {
            return ws.Range[ws.Cells[sRow, sCol], ws.Cells[eRow, eCol]];
        }

       
        // 헤더의 시작 위치 부터 데이터 없는 오른쪽 끝까지 얻어오기
		// 헤더의 시작 위치가 비어 있으면 데이터 있는 오른쪽 끝
        static public Range GetRangeToRight(Worksheet ws, int sRow, int sCol)	
		{
			Range start = (Range)ws.Cells[sRow, sCol];
			Range end = start.End[XlDirection.xlToRight];

			return ws.Range[start, end];			

        }

		static public Range GetRangeToLeft(Worksheet ws, int sRow, int sCol)
        {
            Range start = (Range)ws.Cells[sRow, sCol];
            Range end = start.End[XlDirection.xlToLeft];

            return ws.Range[end, start];
        }

		// 아래쪽 데이터 있느 셀 선택
        static public Range GetRangeToDown(Worksheet ws, int sRow, int sCol)
        {
            Range start = (Range)ws.Cells[sRow, sCol];
            Range end = start.End[XlDirection.xlDown];

            return ws.Range[start, end];

        }

		// 위쪽 데이터있는 셀 선택
        static public Range GetRangeToUp(Worksheet ws, int sRow, int sCol)
        {
            Range start = (Range)ws.Cells[sRow, sCol];
            Range end = (Range)start.End[XlDirection.xlUp];

            return ws.Range[end, start];
        }


        // 우하단 (마지막 모서리 쪽 빈칸이면 바로 윗줄이므로 주의!! - 테이블 우하단 모서리는 아래 GetRangeTableEnd 함수 이용 할 것
        static public Range GetRangeToRightBottom(Worksheet ws, int sRow, int sCol)
		{
            Range start = (Range)ws.Cells[sRow, sCol];
            Range rightEnd = start.End[XlDirection.xlToRight];
            Range rightDownEnd = rightEnd.End[XlDirection.xlDown];

            return ws.Range[start, rightDownEnd];
        }

		// 시작좌표에서 오른쪽 헤더의 끝, key컬럼에서 아래로 Row의 끝
		// 테이블의 모서리 Range를 구할 때 쓴다
        static public Range GetRangeTable(Worksheet ws, int sRow, int sCol, int iColTableKey)
        {
            Range start = (Range)ws.Cells[sRow, sCol];
            Range rightEnd = start.End[XlDirection.xlToRight];

			Range key = (Range)ws.Cells[sRow, iColTableKey];

			Range downEnd;
            // 키 헤더 바로 아래 값없으면 엑셀 시트 마지막 Row까지 가기 때문에 검사럼			
            Range firstValueKey = key.Offset[1, 0]; // 첫번째 키컬럼 데이터
			if(firstValueKey.Value == null)
				downEnd = key;			
			else
				downEnd = key.End[XlDirection.xlDown];

			Range rightDownEnd = (Range)ws.Cells[downEnd.Row, rightEnd.Column];

            return ws.Range[start, rightDownEnd];
        }

        static public Range GetRangeTable(Worksheet ws, int sRow, int sCol, string strTableKey)
        {
			Range key = ws.UsedRange.Find(strTableKey);
			return GetRangeTable(ws, sRow, sCol, key.Column);
        }

		// 엑셀 시트에서 text를 찾아서 왼쪽끝 ~ 오른쪽끝까지 공백없는 라인 범위를 얻을 때 - 헤더 Range 얻기등에 사용하면 편함
        static public Range GetRangeUsedLineWithText(Worksheet ws, string strFindText)
        {
            Range key = ws.UsedRange.Find(strFindText);

            Range start = key.End[XlDirection.xlToLeft];	            
            Range end = key.End[XlDirection.xlToRight];

			return ws.Range[start, end];
        }

        // 자체 Range에서 헤더 얻기
        public Range GetRangeHeader()
		{
            return GetRange(EXCEL_RANGEM.HEADER);
		}        

        static string[] _GetStringArry(Range rng, string strCommentStart, out Dictionary<int, int> dicCommentIdx)
		{
            dicCommentIdx = new Dictionary<int, int>();
            int iCntCols = rng.Columns.Count;

            if (iCntCols <= 0)
            {
                throw new Exception();
			}

            string[] arrStr = new string[iCntCols];
            object[,] data = (object[,])rng.Value;

            int startRow = rng.Row;
            int startCol = rng.Column;
            int commentIdx;

            int idx = 0;
            for (int i = 1; i <= iCntCols; i++)
            {
                string strCell = data[0, 1].ToString();
                strCell = strCell.Trim();
                if (strCell.StartsWith(strCommentStart) == false)
                {
                    arrStr[idx++] = strCell;
                    commentIdx = startRow + i - 1;
                    dicCommentIdx.Add(commentIdx, commentIdx);
                }
            }

            return arrStr;
        }

		// 헤더의 스트링을 얻어온다
		public string[] GetHeaderStringArray(string strCommentStart, out Dictionary<int, int> dicCommentIdx)
		{
			var header = GetRangeHeader();
            return _GetStringArry(header, strCommentStart, out dicCommentIdx);
        }

		static public string[] GetHeaderStringArry(Worksheet ws, int sRow, int sCol, string strCommentStart, out Dictionary<int, int> dicCommentIdx)
		{
			var header = GetRangeToRight(ws, sRow, sCol);
            return _GetStringArry(header, strCommentStart, out dicCommentIdx);
        }


		// 해당 코멘트로 시작되지 않는 리얼 컬럼개수
		public int GetCntCols(string strCommentStart)
		{			
			object[,] tb = (object[,])_rng.Value;

			int iCntComment = 0;
			int iLen = tb.GetLength(1);	// 컬럼 길이

            for (int iCol = 1; iCol <= iLen; iCol++)
			{
				string strCell = tb[1, iCol].ToString();

				strCell = strCell.Trim();
				if (strCell.StartsWith(strCommentStart) == true)
				{
					iCntComment++;
				}
			}

			return iLen - iCntComment;
		}

        // 해당 코멘트로 시작되지 않는 리얼 Row개수
        public int GetCntRows(string strCommentStart)
        {            
            object[,] tb = (object[,])_rng.Value;

            int iCntComment = 0;
            int iLen = tb.GetLength(0);

            for (int iRow = 1; iRow <= iLen; iRow++)
            {
                string strCell = tb[iRow, 1].ToString();

                strCell = strCell.Trim();
                if (strCell.StartsWith(strCommentStart) == true)
                {
                    iCntComment++;
                }
            }

            return iLen - iCntComment;
        }
    }
	
	
	public class ExcelFileM : IDisposable
	{
		public Application _excelApp;
		public Workbook _workBook;		

		public string _excelFilePath = string.Empty;
        private bool disposedValue;

		

        private ExcelFileM(){ }

		

		public static ExcelFileM CreateExcelFile(Application excelApp, string excelFilePath, string sheetName = "")
		{
			ExcelFileM efs = new ExcelFileM();
			return efs.CreateWorkbook(excelApp, excelFilePath, sheetName);
		}


		// 현재 실행파일이 있는 곳에서 파일 읽기
        public static ExcelFileM OpenExcelFileExeFolder(string excelFileName)
		{
			
			Application excelApp = new Application();

			string excelFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, excelFileName);
			//
			//string excelFilePath = AppDomain.CurrentDomain.BaseDirectory + "\\\\" + excelFileName;

			//string excelFilePath = "C:\_mine\GitHubProject\EcsServerTang\bin\Debug\net8.0-windows\TangTable.xlsx";


			return OpenExcelFile(excelApp, excelFilePath);

		}

        public static ExcelFileM OpenExcelFile(Application excelApp, string excelFilePath)
		{
			ExcelFileM efs = new ExcelFileM();	

            return efs.OpenWorkbook(excelApp, excelFilePath);
		}

		public static ExcelFileM OpenGlobalExcelFile(Application excelApp)
        {
			ExcelFileM efs = new ExcelFileM();
            			
            return efs.OpenWorkbook(excelApp);            
        }


		private ExcelFileM CreateWorkbook(Application excelApp, string excelFilePath, string sheetName = "")
		{
			_excelApp = excelApp;
			_workBook = _excelApp.Workbooks.Add();			
			Worksheet sheet = (Worksheet)_workBook.Sheets.get_Item(1); 

			if (sheetName != string.Empty)
			{
				sheet.Name = sheetName;
			}
			
			return this;
		}

		//private void CreateProgressBar()
		//{   
  //          _progressBar = new System.Windows.Forms.ProgressBar();
  //          _progressBar.Minimum = 0;
  //          _progressBar.Maximum = 100;
  //          _progressBar.Step = 1;
  //          _progressBar.Visible = true;

		//	_excelApp
  //      }
  		

        private ExcelFileM OpenWorkbook(Application excelApp)
        {
            _excelApp = excelApp;
			_workBook = _excelApp.ActiveWorkbook;

            TerminateEditing();
           
            return this;
        }

        private ExcelFileM OpenWorkbook(Application excelApp, string excelFilePath)
		{
            
            _excelApp = excelApp;
			_excelFilePath = excelFilePath;
			_workBook = _excelApp.Workbooks.Open(@_excelFilePath);

			TerminateEditing();			

			return this;
		}

		public void RefreshAll()
		{
			_excelApp.ActiveWorkbook.RefreshAll();
		}

		public bool IsActiveWorkSheet(string sheetName)
		{
			Worksheet wsActive = (Worksheet)_excelApp.ActiveSheet;
			return wsActive.Name == sheetName;
		}

		public void CreateWorksheet(string sheetName)
		{
			Worksheet ws = (Worksheet)_workBook.Worksheets.Add();
			ws.Name = sheetName;
			
		}

		public Worksheet GetWorksheet(string sheetName)
		{
			return (Worksheet)_workBook.Sheets[sheetName];
			
		}

		public enum E_USER_CONTROL_ID
		{
			PROGRESS_BAR,
		}
#if NETFRAMEWORK
		// 추가된 컨트롤
		Dictionary<E_USER_CONTROL_ID, Control> _dicAddControl = new Dictionary<E_USER_CONTROL_ID, Control>();
		public ProgressBar _progressBar;
		Range _rngProgressBarText;

		//public void SetAllProgress(string progressbarText, int iVal, int iMaxVal)
		//{
		//	var pBarText = _progressBar.GetBarFormatText(progressbarText, iVal, iMaxVal);

  //          SetProgressBarText(pBarText);
  //          SetStatusBar(pBarText);
  //          _progressBar.SetValue(iVal, iMaxVal);
  //      }

        public void SetProgressBarText(string text)
		{
			if(_rngProgressBarText != null)
			{
                _rngProgressBarText.Value = text;
                //if (_rngProgressBarText.Locked)
                //{
                //                _rngProgressBarText.Worksheet.Unprotect();
                //                _rngProgressBarText.Locked = false;					
                //	_rngProgressBarText.Value = text;
                //                _rngProgressBarText.Locked = true;
                //                _rngProgressBarText.Worksheet.Protect();
                //            }
                //else
                //{
                //                _rngProgressBarText.Value = text;
                //            }
            }

			return;
		}

		// 컨트롤 얻기
		public Control GetControl(E_USER_CONTROL_ID eControlId)
		{
			if(_dicAddControl.TryGetValue(eControlId, out Control control) == true)
				return control;

			return null;
		}
#endif

		//     public void AddControl(string sheetName, Control control, E_USER_CONTROL_ID eControlId, Range rngProgress, Range rngProgressBarText = null) // controlName은 추후 삭제 할 때 씀
		//     {

		//var ws = GetWorksheet(sheetName);
		//Microsoft.Office.Tools.Worksheet vstoWs = Globals.Factory.GetVstoObject(ws);   // 알수가 없구나 왜 다른 sheet를 뱉어내니


		//         if (_dicAddControl.ContainsKey(eControlId) == false)
		//{

		//	if (eControlId == E_USER_CONTROL_ID.PROGRESS_BAR)
		//	{
		//		_progressBar = control as ProgressBarM; // 초기화 
		//		if (rngProgressBarText != null)
		//			_rngProgressBarText = rngProgressBarText;
		//	}

		//	_dicAddControl.Add(eControlId, control);
		//             vstoWs.Controls.AddControl(control, rngProgress, eControlId.ToString());
		//	//if (rngProgress.Locked) // Range 잠겼으면
		//	//{
		//	//	rngProgress.Worksheet.Unprotect();
		//	//	rngProgress.Locked = false;
		//	//	vstoWs.Controls.AddControl(control, rngProgress, eControlId.ToString());
		//	//	rngProgress.Locked = true;
		//	//	rngProgress.Worksheet.Protect();
		//	//}
		//	//else
		//	//{
		//	//	vstoWs.Controls.AddControl(control, rngProgress, eControlId.ToString());
		//	//}
		//}
		//else
		//{
		//	Debug.WriteLine("이미 추가된 컨트롤입니다.");
		//	return;
		//}
		//     }

		public void SetStatusBar(string text)
		{
			_excelApp.StatusBar = text;
		}


		public void Save()
        {
			//_wb.Close(true);			
			_workBook.SaveAs(_excelFilePath);
        }

		public void Release(Boolean bExcelAppRelease = false)
        {
			var enumerator = _workBook.Worksheets.GetEnumerator();
			
			while(enumerator.MoveNext())
            {
				var sheet = enumerator.Current;
				ReleaseExcelObject(sheet);
            }

			_workBook.Close(true);   // 변경점 저장후 닫기 true
			ReleaseExcelObject(_workBook);

			if(bExcelAppRelease)
            {
				_excelApp.Quit();
				ReleaseExcelObject(_excelApp);
            }
		}

		private static void ReleaseExcelObject(object obj)
		{
			try
			{
				if (obj != null)
				{
					Marshal.ReleaseComObject(obj);
					obj = null;
				}
			}
			catch (Exception ex)
			{
				obj = null;
				throw ex;
			}
			finally
			{
				GC.Collect();
			}
		}

        //static public T[] ReadSheet<T>(string excelPath, string sheetName) where T : LoadableData, new()
        //      {
        //	if (_connection == null)	// close 하면 null이 된다. 
        //	{
        //		_connection = Open(excelPath);
        //		if (_connection == null)
        //		{
        //			Debug.WriteLine("Excel load fail (" + excelPath + ")");
        //			return null;
        //		}
        //	}

        //	try
        //	{

        //		DataTable dataTable = OpenSheet(_connection, sheetName);
        //		if (dataTable == null) throw new Exception();
        //		T[] sheetData = LoadSheet<T>(dataTable);

        //		return sheetData;
        //	}
        //	catch (Exception e)
        //	{
        //		Debug.WriteLine(string.Format("Excel sheet load fail ({0}/{1})\r\n{2}",
        //									  excelPath, e.Message, e.StackTrace));
        //		_connection.Close();				
        //		return null;
        //	}
        //}


        void worksheet_BeforeDoubleClick(object ws,  Range Target, ref bool Cancel)
        {
            Cancel = true; // Cancel the double click event
        }


		
        public void SetSheetEditing(string sheetName, bool bSheetEditing)
		{

			var ws = GetWorksheet(sheetName);						
			if (ws == null)
			{
				Debug.WriteLine("시트 이름을 확인하세요");
				return;
			}

			if (bSheetEditing == false)  // 편집 막기
			{   
                TerminateEditing();

				
				_excelApp.SheetBeforeDoubleClick += worksheet_BeforeDoubleClick;	// 더블클릭 막기

                // Set the UserInterfaceOnly property to true
                ws.Protect(Type.Missing, Type.Missing, Type.Missing, Type.Missing, true, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing);

				// Disable editing
				_excelApp.ScreenUpdating = false;				
				_excelApp.EnableEvents = false;
				_excelApp.DisplayAlerts = false;
			}
            else // 편집 실행            
			{
                // Call Unprotect to remove protection from the worksheet
                ws.Unprotect(Type.Missing);

                // Disable editing
                _excelApp.ScreenUpdating = true;
                //_excelApp.SheetBeforeDoubleClick -= worksheet_BeforeDoubleClick;	// 더블클릭 허용
                _excelApp.EnableEvents = true;
                _excelApp.DisplayAlerts = true;
            }

			return;
			
        }


        // 편집중에 부르면 예외 발생
        public void SetInteractive(bool bInteractive)
        {
			if(bInteractive == false)
				TerminateEditing();

			if(_excelApp.Interactive != bInteractive)
                _excelApp.Interactive = bInteractive;
        }

        public void TerminateEditing()
		{
            _excelApp.ActiveCell.Activate();
#if NETFRAMEWORK
			SendKeys.SendWait("{ESC}");
#endif
        }

		static public Range Find(Worksheet ws, string findStr)
		{
			return ws.UsedRange.Find(findStr);
		}

		public Range Find(string sheetName, string findStr)
		{
			return Find(GetWorksheet(sheetName), findStr);
		}

		public Range FindTableRange(string sheetName, string findStr, string strKey)
		{
			var ws = GetWorksheet(sheetName);            
			var rngKey = Find(ws, strKey);

			return FindTableRange(sheetName, findStr, rngKey.Column);
        }

        public Range FindTableRange(string sheetName, string findStr, int iColTableKey)
        {
            var ws = GetWorksheet(sheetName);
            var rngStart = Find(ws, findStr);
            Range rngTable = ExcelRangeM.GetRangeTable(ws, rngStart.Row, rngStart.Column, iColTableKey);

            return rngTable;
        }


		// 데이터 영역만 얻기
        // leftTop string부터 rightBottome Range에서 헤더 제외한 데이터 영역
        public Range FindTableDataRange(string sheetName, string strLeftTop, string strKey)
        {
            var ws = GetWorksheet(sheetName);
            var rngKey = Find(ws, strKey);
			return FindTableDataRange(sheetName, strLeftTop, rngKey.Column);
        }

        // 데이터 영역만 얻기
        public Range FindTableDataRange(string sheetName, string strLeftTop, int iColTableKey)
        {
            var ws = GetWorksheet(sheetName);
            var rngStart = Find(ws, strLeftTop);            
            Range rngTable = ExcelRangeM.GetRangeTable(ws, rngStart.Row, rngStart.Column, iColTableKey);

            Range dataRng = new ExcelRangeM(rngTable).GetRange(ExcelRangeM.EXCEL_RANGEM.WITHOUT_HEADER);

            return dataRng;
        }

        // 컬럼 및 ROW가 #으로 시작되면 그 열과 행은 테이블에서 제외한다.
        // 제일 첫번째 key 컬럼 데이터가 없으면 테이블은 데이터가 없다고 판단한다.
        public ExcelTableM GetExcelTable(string sheetName, string strLeftTop, string strTableKey = null)
        {
            TerminateEditing();

            if (strTableKey == null)
                strTableKey = strLeftTop;

            Worksheet ws = GetWorksheet(sheetName);
            if (ws == null)
            {
                Debug.WriteLine("sheet 이름을 확인하세요");
                return null;
            }

            Range rngLeftTop = ws.UsedRange.Find(strLeftTop);  // cell 찾기 - 테이블의 제일 좌측상단 텍스트

            if (rngLeftTop == null)
            {
                Debug.WriteLine("LeftTop의 문자열을 확인하세요");
                return null;
            }

            //Range rngTable = rngKey.CurrentRegion; // 테이블 얻기 (빈공간으로 둘러쌓인곳)
            Range rngTable = ExcelRangeM.GetRangeTable(ws, rngLeftTop.Row, rngLeftTop.Column, strTableKey);            
			ExcelTableM tb = new ExcelTableM(rngTable, "#", strTableKey);

            return tb;
        }

        // 컬럼 및 ROW가 #으로 시작되면 그 열과 행은 테이블에서 제외한다.
        // 제일 첫번째 key 컬럼 데이터가 없으면 테이블은 데이터가 없다고 판단한다.
        public T[] ReadSheetData<T>(string sheetName, string strLeftTop, string strTableKey = null) where T : LoadableDataInStructM, new()
		{
			TerminateEditing();

			if (strTableKey == null)
				strTableKey = strLeftTop;

            T[] rtn = null;

            Worksheet ws = GetWorksheet(sheetName);
			if(ws == null)
			{
				Debug.WriteLine("sheet 이름을 확인하세요");
				return null;
			}
						
            Range rngLeftTop = ws.UsedRange.Find(strLeftTop);  // cell 찾기 - 테이블의 제일 좌측상단 텍스트

			if (rngLeftTop == null)
			{
                Debug.WriteLine("LeftTop의 문자열을 확인하세요");
				return null;
			}			


            //Range rngTable = rngKey.CurrentRegion; // 테이블 얻기 (빈공간으로 둘러쌓인곳)
            Range rngTable = ExcelRangeM.GetRangeTable(ws, rngLeftTop.Row, rngLeftTop.Column, strTableKey);

   //         if (rngKey.Row != rngTable.Row || rngKey.Column != rngTable.Column)	// 둘러쌓인 공간이 주석등에 의해서 더 크면 key가 있는 좌상단 부터 테이블의 하단까지 
			//{				
			//	rngTable = ws.Range[ws.Cells[rngKey.Row, rngKey.Column], ws.Cells[rngTable.Row + rngTable.Rows.Count - 1, rngTable.Column + rngTable.Columns.Count - 1]];
			//}

			ExcelTableM tb = new ExcelTableM(rngTable, "#", strTableKey);						
            try
			{
				rtn = tb.ReadTable<T>();
			}
			catch(TypeLoadException e)
			{
				throw e;
			}

			return rtn;
		}

        // 컬럼 및 ROW가 #으로 시작되면 그 열과 행은 테이블에서 제외한다.        
        public T ReadSheetDataHeader<T>(string sheetName, string strLeftTop, string strTableKey = null) where T : LoadableDataInStructM, new()
        {
            TerminateEditing();

            if (strTableKey == null)
                strTableKey = strLeftTop;

            T rtn = null;

            Worksheet ws = GetWorksheet(sheetName);
            if (ws == null)
            {
                Debug.WriteLine("sheet 이름을 확인하세요");
                return null;
            }

            Range rngLeftTop = ws.UsedRange.Find(strLeftTop);  // cell 찾기 - 테이블의 제일 좌측상단 텍스트

            if (rngLeftTop == null)
            {
                Debug.WriteLine("LeftTop의 문자열을 확인하세요");
                return null;
            }

            //Range rngTable = rngKey.CurrentRegion; // 테이블 얻기 (빈공간으로 둘러쌓인곳)
			Range rngHeader = ExcelRangeM.GetRangeToRight(ws, rngLeftTop.Row, rngLeftTop.Column);			            

            ExcelTableM tb = new ExcelTableM(rngHeader, "#", strTableKey);
            try
            {
                rtn = tb.ReadTableHeader<T>();
            }
            catch (TypeLoadException e)
            {
                throw e;
            }

            return rtn;
        }


        // 시트 이름과 헤더 좌상단 key, T[] 데이터를 받아서 엑셀에 쓰는 함수
        public void WriteSheetData<T>(T[] data, string sheetName, string strLeftTop, string strTableKey = null) where T : LoadableDataInStructM
		{
			TerminateEditing();
            
			if (strTableKey == null)
                strTableKey = strLeftTop;

            Worksheet ws = GetWorksheet(sheetName);
			//ws.Calculate();
			            
            Range rngLeftTop = ws.UsedRange.Find(strLeftTop);  // cell 찾기 - 테이블의 제일 좌측상단 텍스트

            if (rngLeftTop == null)
            {
                Debug.WriteLine("LeftTop의 문자열을 확인하세요");
                return;
            }

            //Range rngTable = rngKey.CurrentRegion; // 테이블 얻기 (빈공간으로 둘러쌓인곳)
            Range rngTable = ExcelRangeM.GetRangeTable(ws, rngLeftTop.Row, rngLeftTop.Column, strTableKey);            

			Range rngHeader = ExcelRangeM.GetRangeToRight(ws, rngTable.Row, rngTable.Column);

			ExcelTableM tb = new ExcelTableM(rngHeader, "#", strTableKey);
			var dicCommentIdx = tb.GetDicCommentHeaderIdxInExcel();
			var arrHeaderStr = tb.GetHeaderString();

			Range rngWriteData = ExcelRangeM.GetRange(ws, rngTable.Row + 1, rngTable.Column, rngTable.Row + data.GetLength(0), rngTable.Column + rngTable.Columns.Count);

			ExcelRangeM rngWriteM = new ExcelRangeM(rngWriteData);
			rngWriteM.WriteData(data, arrHeaderStr, dicCommentIdx);

        }



		~ExcelFileM()
		{			
			Dispose(false);
		}

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
					// TODO: 관리형 상태(관리형 개체)를 삭제합니다.
#if NETFRAMEWORK
					var enControl = _dicAddControl.Values.GetEnumerator();
                    while (enControl.MoveNext())
					{
						var control = enControl.Current;
						control.Dispose();
					}
#endif
                }

				// TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
				// TODO: 큰 필드를 null로 설정합니다.
				Release(true); // 엑셀 모두를 릴리즈

                disposedValue = true;
            }
        }

        // // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
        // ~ExcelFileM()
        // {
        //     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }


	///// <summary>
	///// moon(4)라고 입력하면 name = "moon", index = 4;
	///// </summary>

	//public class ClassMemberInfo
	//{
	//	public string _name = string.Empty;
	//	public int _idx = -1;

	//	public bool HasIndex()
	//	{
	//		return _idx >= 0;
	//	}

	//	public void Parse(string memberName)
	//	{
	//		string indexPattern = "\\([0-9]+\\)$";      // "\\[[0-9]+\\]"를 사용하고 싶은데 excel이 맘대로 '[' -> '(', ']' -> ')'로 치환해 버린다;
	//		string indexValuePattern = "[0-9]+";

	//		_name = memberName;

	//		// Index Pattern Check
	//		Match match = Regex.Match(_name, indexPattern);
	//		string matchString = match.ToString();
	//		if (string.IsNullOrEmpty(matchString) == true)
	//			return;

	//		Match subMatch = Regex.Match(matchString, indexValuePattern);
	//		string subMatchString = subMatch.ToString();
	//		_idx = int.Parse(subMatchString);

	//		_name = Regex.Replace(_name, indexPattern, string.Empty);
	//	}
	//}

	///// <summary>
	///// 1. 텍스트 끝에 (U) 있으면 _IsUnique  true out
	///// 2. .또는 #으로 분리되어 있으면 위 ClassMember 배열로 만듬
	///// </summary>
	////"a(1).b(2).c(3)(U)
	//public class TableColumnInfo
	//{
	//	static private string uniquePattern = "\\(U\\)$";           // "\\[U]\\]"를 사용하고 싶은데 excel이 맘대로 '[' -> '(', ']' -> ')'로 치환해 버린다;
	//	static private char[] separator = new char[] { '.', '#' };  // '.'만 사용하고 싶은데 excel이 맘대로 '.' -> '#'로 치환해 버린다;

	//	public ClassMemberInfo[] arrClassMemberInfo = null;

	//	public void Parse(string _ColumnIndexHead, ref bool bUnique)
	//	{
	//		// Unique Pattern Check
	//		bUnique = Regex.IsMatch(_ColumnIndexHead, uniquePattern);
	//		if (bUnique == true)
	//			_ColumnIndexHead = Regex.Replace(_ColumnIndexHead, uniquePattern, string.Empty);

	//		string[] tokenArray = _ColumnIndexHead.Split(separator);
	//		arrClassMemberInfo = new ClassMemberInfo[tokenArray.Length];

	//		for (int i = 0; i < tokenArray.Length; i++)
	//		{
	//			arrClassMemberInfo[i] = new ClassMemberInfo();
	//			arrClassMemberInfo[i].Parse(tokenArray[i]);
	//		}
	//	}
	//}

 //   /// <summary>
 //   /// 컬럼명들의 Array를 받아서 Parse
 //   /// 1. 컬럼Info의 배열을 만듬
 //   /// 2. 클래스멤버이름[4] 통째로 key, isUnique && .#분리안됨 --> "(U)" 있는 값 그리고 클래스멤버 배열 lengh 1일 때 true 아니면 false값 가지고 _dicClassMemberName 만듬
	///// 3. 위 값이 flase 일 때는 클래스멤버이름#클래스멤버이름#클래스멤버이름 을 key로, 값을 false로 _dicClassMemberName 만듬
	///// --- 즉 Moon(4)(U)    Moom(4), true               Kim(4)#goo(5)(U)  <--- (U)는 무시됨   Kim(4), false ---- Kim(4)#goo(5), false   이렇게 dic 만들어짐
 //   /// </summary>
 //   // a[0], true  
 //   public class TableHeadInfo
	//{
	//	private Dictionary<string, bool> _dicClassMemberName = null;
	//	public TableColumnInfo[] _columnInfoArray = null;
	//	public int _uniqueIndex = -1;

	//	public bool IsUniqueInRow()
	//	{
	//		return _uniqueIndex >= 0;
	//	}

	//	public void Parse(string[] arrColumnIndexHead)
	//	{
	//		_dicClassMemberName = new Dictionary<string, bool>();
	//		_columnInfoArray = new TableColumnInfo[arrColumnIndexHead.Length];
	//		_uniqueIndex = -1;

	//		for (int i = 0; i < arrColumnIndexHead.Length; i++)
	//		{
	//			bool isUnique = false;

	//			_columnInfoArray[i] = new TableColumnInfo();
	//			_columnInfoArray[i].Parse(arrColumnIndexHead[i], ref isUnique);
	//			AddClassMemberName(i, isUnique);
	//		}
	//	}

	//	public bool IsUniqueClassMember(string memberNameKey)
	//	{
	//		return _dicClassMemberName.ContainsKey(memberNameKey) == true && _dicClassMemberName[memberNameKey] == true;
	//	}

	//	private void AddClassMemberName(int columnIndex, bool bUnique)
	//	{
	//		ClassMemberInfo[] classMemberInfoArray = _columnInfoArray[columnIndex].arrClassMemberInfo;

	//		if (classMemberInfoArray.Length == 1 && bUnique == true)
	//			_uniqueIndex = columnIndex;

	//		string classMemberName = string.Empty;

	//		for (int i = 0; i < classMemberInfoArray.Length; i++)
	//		{
	//			string name = classMemberInfoArray[i]._name;

	//			if (classMemberInfoArray[i].HasIndex() == true)
	//				name += "[" + classMemberInfoArray[i]._idx.ToString() + "]";

	//			if (string.IsNullOrEmpty(classMemberName) == true)
	//				classMemberName = name;
	//			else
	//				classMemberName = classMemberName + "#" + name;

	//			if (_dicClassMemberName.ContainsKey(classMemberName) == false)
	//			{
	//				if (IsUniqueInRow() && bUnique == true)
	//					_dicClassMemberName.Add(classMemberName, true);
	//				else
	//					_dicClassMemberName.Add(classMemberName, false);
	//			}
	//		}
	//	}
	//}

	/// <summary>
	/// ////////////////////////////////////////////////////////////////////////////////
	/// </summary>


	//public class TableParser
	//{
	//	private TableHeadInfo m_HeadInfo = null;
	//	private Dictionary<string, object> m_UniqueObjectPool = null;
	//	private Dictionary<string, object> m_RowObjectPool = null;
	//	private Dictionary<string, string> m_MemberValuePool = null;

	//	public string UniqueRowKey = string.Empty;
	//	public string MemberNameKey = string.Empty;
	//	public string ValueNameKey = string.Empty;

	//	private void UpdateUniqueRowKey(string _Value, int _Column)
	//	{
	//		if (m_HeadInfo.IsUniqueInRow() == true && m_HeadInfo._uniqueIndex == _Column)
	//			UniqueRowKey = _Value;
	//	}

	//	private void UpdateMemberNameKey(string _MemberName, bool _HasIndex, int _Index)
	//	{
	//		string name = _MemberName;
	//		if (_HasIndex == true)
	//		{
	//			name += "[" + _Index.ToString() + "]";
	//		}

	//		if (string.IsNullOrEmpty(MemberNameKey) == true)
	//			MemberNameKey = name;
	//		else
	//			MemberNameKey = MemberNameKey + "#" + name;
	//	}

	//	private void UpdateValueNameKey(string _MemberName, string _Value, bool _HasIndex, int _Index)
	//	{
	//		string memberName = _MemberName;
	//		if (_HasIndex == true)
	//		{
	//			memberName += "[" + _Index.ToString() + "]";
	//		}
	//		string name = memberName + "." + _Value;

	//		if (m_MemberValuePool.ContainsKey(memberName) == false)
	//			m_MemberValuePool.Add(memberName, name);
	//		else
	//			name = m_MemberValuePool[memberName];

	//		if (string.IsNullOrEmpty(ValueNameKey) == true)
	//		{
	//			if (m_HeadInfo.IsUniqueInRow() == true)
	//				ValueNameKey = UniqueRowKey + "#" + name;
	//			else
	//				ValueNameKey = name;
	//		}
	//		else
	//			ValueNameKey = ValueNameKey + "#" + name;
	//	}

	//	public void ParseHead(string[] _ColumnIndexHead)
	//	{
	//		m_UniqueObjectPool = new Dictionary<string, object>();
	//		m_HeadInfo = new TableHeadInfo();
	//		m_HeadInfo.Parse(_ColumnIndexHead);
	//	}

	//	public void ResetRowInfo()
	//	{
	//		m_MemberValuePool = new Dictionary<string, string>();
	//		m_RowObjectPool = new Dictionary<string, object>();
	//		UniqueRowKey = string.Empty;
	//		MemberNameKey = string.Empty;
	//	}

	//	public void ResetColumnInfo()
	//	{
	//		MemberNameKey = string.Empty;
	//		ValueNameKey = string.Empty;
	//	}

	//	public T GetRowData<T>(List<T> _ResultList, object _Object) where T : LoadableExcelData, new()
	//	{
	//		string key = string.Empty;

	//		if (m_HeadInfo.IsUniqueInRow() == true)
	//		{
	//			key = _Object.ToString();

	//			if (string.IsNullOrEmpty(key) == true)
	//				return default(T);

	//			if (m_UniqueObjectPool.ContainsKey(key) == true)
	//				return (T)m_UniqueObjectPool[key];
	//		}

	//		T rowData = new T();
	//		_ResultList.Add(rowData);

	//		if (m_HeadInfo.IsUniqueInRow() == true)
	//			m_UniqueObjectPool.Add(key, rowData);

	//		return rowData;
	//	}

	//	public bool CanParse(int _ColumnIndex, object _Object)
	//	{
	//		if (_Object.GetType() == typeof(System.DBNull))
	//			return false;

	//		// 널 문자에 대한 예약문자;
	//		if (_Object.GetType() == typeof(string) && (string)_Object == "-")
	//			return false;

	//		if (string.IsNullOrEmpty(m_HeadInfo._columnInfoArray[_ColumnIndex].arrClassMemberInfo[0]._name))
	//			return false;

	//		// 사용하지 않을 Column에 대한 예약문자;
	//		if (m_HeadInfo._columnInfoArray[_ColumnIndex].arrClassMemberInfo[0]._name[0] == '_')
	//			return false;

	//		return true;
	//	}

	//	public bool Parse(int _ColumnIndex, object _Object, object _RowData)
	//	{
	//		return Parse(_ColumnIndex, _Object, _RowData, 0);
	//	}

	//	private bool Parse(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex)
	//	{
	//		if (_TargetObject == null)
	//			return false;

	//		TableColumnInfo columnInfo = m_HeadInfo._columnInfoArray[_ColumnIndex];
	//		ClassMemberInfo classMemberInfo = columnInfo.arrClassMemberInfo[_ClassMemberIndex];

	//		bool isLastMember = _ClassMemberIndex == columnInfo.arrClassMemberInfo.Length - 1;

	//		UpdateUniqueRowKey(_Object.ToString(), _ColumnIndex);
	//		UpdateMemberNameKey(classMemberInfo._name, classMemberInfo.HasIndex(), classMemberInfo._idx);
	//		UpdateValueNameKey(classMemberInfo._name, _Object.ToString(), classMemberInfo.HasIndex(), classMemberInfo._idx);

	//		if (isLastMember)
	//			return SetData(_ColumnIndex, _Object, _TargetObject, _ClassMemberIndex);
	//		else
	//			return Parse(_ColumnIndex, _Object, GetNextTargetObject(_ColumnIndex, _Object, _TargetObject, _ClassMemberIndex), _ClassMemberIndex + 1);
	//	}

	//	private object DoMethod(object _TargetObject, string _MethodName, object[] _ParamArray)
	//	{
	//		MethodInfo methodInfo = _TargetObject.GetType().GetMethod(_MethodName);
	//		if (methodInfo == null)
	//			return null;

	//		return methodInfo.Invoke(_TargetObject, _ParamArray);
	//	}

	//	private object DoMethod(object _TargetObject, string _MethodName, Type[] _TypeArray, object[] _ParamArray)
	//	{
	//		MethodInfo methodInfo = _TargetObject.GetType().GetMethod(_MethodName, _TypeArray);
	//		if (methodInfo == null)
	//			return null;

	//		return methodInfo.Invoke(_TargetObject, _ParamArray);
	//	}

	//	private object GetNewObject(Type _FieldType, object _Object)
	//	{
	//		if (_FieldType.IsEnum == true)
	//			return Enum.Parse(_FieldType, _Object.ToString());

	//		return Convert.ChangeType(_Object, _FieldType);
	//	}

	//	private bool SetData(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex)
	//	{
	//		TableColumnInfo columnInfo = m_HeadInfo._columnInfoArray[_ColumnIndex];
	//		ClassMemberInfo classMemberInfo = columnInfo.arrClassMemberInfo[_ClassMemberIndex];
	//		FieldInfo fieldInfo = _TargetObject.GetType().GetField(classMemberInfo._name);
	//		if (fieldInfo == null)
	//			return false;
	//		object fieldObject = fieldInfo.GetValue(_TargetObject);

	//		if (fieldInfo.FieldType.IsArray == true)
	//		{
	//			Type elementType = fieldInfo.FieldType.GetElementType();
	//			object newObject = GetNewObject(elementType, _Object);

	//			AddElementToArray(fieldInfo, fieldObject, _TargetObject, newObject, classMemberInfo.HasIndex(), classMemberInfo._idx);
	//		}
	//		else if (fieldInfo.FieldType.IsGenericType == true)
	//		{
	//			if (fieldObject == null)
	//			{
	//				fieldObject = Activator.CreateInstance(fieldInfo.FieldType);
	//				fieldInfo.SetValue(_TargetObject, fieldObject);
	//			}

	//			Type elementType = fieldInfo.FieldType.GetGenericArguments()[0];

	//			object newObject = GetNewObject(elementType, _Object);
	//			DoMethod(fieldObject, "Add", new object[1] { newObject });
	//		}
	//		else
	//		{
	//			object newObject = GetNewObject(fieldInfo.FieldType, _Object);
	//			fieldInfo.SetValue(_TargetObject, newObject);
	//		}

	//		return true;
	//	}

	//	private object GetNextTargetObject(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex)
	//	{
	//		TableColumnInfo columnInfo = m_HeadInfo._columnInfoArray[_ColumnIndex];
	//		ClassMemberInfo classMemberInfo = columnInfo.arrClassMemberInfo[_ClassMemberIndex];

	//		FieldInfo fieldInfo = _TargetObject.GetType().GetField(classMemberInfo._name);
	//		if (fieldInfo == null)
	//			return null;

	//		object fieldObject = fieldInfo.GetValue(_TargetObject);

	//		if (fieldInfo.FieldType.IsArray == true)
	//		{
	//			bool isExist = true;
	//			object newObject = GetElementObject(_ColumnIndex, _Object, fieldObject, _ClassMemberIndex, fieldInfo, ref isExist);
	//			if (newObject == null)
	//				return null;

	//			if (isExist == true)
	//				return newObject;

	//			AddElementToArray(fieldInfo, fieldObject, _TargetObject, newObject, classMemberInfo.HasIndex(), classMemberInfo._idx);

	//			return newObject;
	//		}
	//		else if (fieldInfo.FieldType.IsGenericType == true)
	//		{
	//			bool isExist = true;
	//			object newObject = GetElementObject(_ColumnIndex, _Object, fieldObject, _ClassMemberIndex, fieldInfo, ref isExist);
	//			if (newObject == null)
	//				return null;

	//			if (isExist == true)
	//				return newObject;

	//			if (fieldObject == null)
	//			{
	//				fieldObject = Activator.CreateInstance(fieldInfo.FieldType);
	//				fieldInfo.SetValue(_TargetObject, fieldObject);
	//				fieldObject = fieldInfo.GetValue(_TargetObject);
	//			}

	//			DoMethod(fieldObject, "Add", new object[1] { newObject });
	//			return newObject;
	//		}
	//		else
	//		{
	//			if (fieldObject == null)
	//			{
	//				fieldObject = Activator.CreateInstance(fieldInfo.FieldType);
	//				fieldInfo.SetValue(_TargetObject, fieldObject);
	//			}

	//			return fieldObject;
	//		}
	//	}

	//	private void AddElementToArray(FieldInfo _ArrayFieldInfo, object _ArrayObject, object _TargetObject, object _NewObject, bool _HasIndex, int _Index)
	//	{
	//		Type elementType = _ArrayFieldInfo.FieldType.GetElementType();
	//		Array newArray = null;
	//		int index = 0;
	//		int size = 0;
	//		int newSize = 0;

	//		if (_ArrayObject == null)
	//		{
	//			if (_HasIndex == true)
	//			{
	//				index = _Index;
	//				newSize = index + 1;
	//			}
	//			else
	//			{
	//				index = 0;
	//				newSize = 1;
	//			}

	//			newArray = Array.CreateInstance(elementType, newSize);
	//			_ArrayFieldInfo.SetValue(_TargetObject, newArray);
	//			_ArrayObject = _ArrayFieldInfo.GetValue(_TargetObject);
	//		}
	//		else
	//		{
	//			size = (int)DoMethod(_ArrayObject, "GetLength", new object[1] { 0 });
	//			if (_HasIndex == true)
	//			{
	//				index = _Index;
	//				if (index < size)
	//					newSize = size;
	//				else
	//					newSize = index + 1;
	//			}
	//			else
	//			{
	//				index = size;
	//				newSize = size + 1;
	//			}

	//			if (newSize != size)
	//			{
	//				newArray = Array.CreateInstance(elementType, newSize);
	//				Array.Copy((Array)_ArrayObject, newArray, size);
	//				_ArrayFieldInfo.SetValue(_TargetObject, newArray);
	//				_ArrayObject = _ArrayFieldInfo.GetValue(_TargetObject);
	//			}
	//		}

	//		DoMethod(_ArrayObject, "SetValue", new Type[] { typeof(object), typeof(int) }, new object[2] { _NewObject, index });
	//	}

	//	private object GetElementObject(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex, FieldInfo _FieldInfo, ref bool _IsExist)
	//	{
	//		object elementObject = null;

	//		if (m_HeadInfo.IsUniqueClassMember(MemberNameKey) == true)
	//		{
	//			if (m_UniqueObjectPool.ContainsKey(ValueNameKey) == true)
	//				elementObject = m_UniqueObjectPool[ValueNameKey];
	//		}

	//		if (elementObject == null)
	//		{
	//			if (m_RowObjectPool.ContainsKey(ValueNameKey) == true)
	//				elementObject = m_RowObjectPool[ValueNameKey];
	//		}

	//		if (elementObject == null)
	//		{
	//			_IsExist = false;
	//			Type elementType;
	//			// 현재 버전은 Array와 List만 처리할 수 있다;
	//			if (_FieldInfo.FieldType.IsArray == true)
	//				elementType = _FieldInfo.FieldType.GetElementType();
	//			else if (_FieldInfo.FieldType.GetGenericTypeDefinition() == typeof(List<>))
	//				elementType = _FieldInfo.FieldType.GetGenericArguments()[0];
	//			else
	//				return null;

	//			if (elementType.IsArray == true)
	//				elementObject = Array.CreateInstance(elementType, 0);
	//			else
	//				elementObject = Activator.CreateInstance(elementType);

	//			if (m_HeadInfo.IsUniqueClassMember(MemberNameKey) == true)
	//				m_UniqueObjectPool.Add(ValueNameKey, elementObject);

	//			m_RowObjectPool.Add(ValueNameKey, elementObject);
	//		}

	//		return elementObject;
	//	}
	//}




    //public class ExcelLibM
    //   {

    //	public Application excelApp;

    //	// 싱글톤
    //	private static ExcelLibM instance = new ExcelLibM();

    //	public static ExcelLibM GetInstance()
    //	{
    //		return instance;
    //	}

    //	private ExcelLibM()
    //       {
    //		excelApp = new Application();
    //		// 워크북 오픈시 파일 열기
    //		// excelApp.Visible = true;
    //       }



    //	private static void ReleaseExcelObject(object obj)
    //	{
    //		try
    //		{
    //			if (obj != null)
    //			{
    //				Marshal.ReleaseComObject(obj);
    //				obj = null;
    //			}
    //		}
    //		catch (Exception ex)
    //		{
    //			obj = null;
    //			throw ex;
    //		}
    //		finally
    //		{
    //			GC.Collect();
    //		}
    //	}


    //	static public void Close()
    //	{
    //		if (_connection != null)
    //		{
    //			_connection.Close();
    //			_connection = null;
    //		}
    //	}

    //	static public void Close(OdbcConnection _Connection)
    //	{
    //		_Connection.Close();
    //	}

    //	static public T[] ReadSheet<T>(string excelPath, string sheetName) where T : LoadableData, new()
    //	{
    //		if (_connection == null)    // close 하면 null이 된다. 
    //		{
    //			_connection = Open(excelPath);
    //			if (_connection == null)
    //			{
    //				Debug.WriteLine("Excel load fail (" + excelPath + ")");
    //				return null;
    //			}
    //		}

    //		try
    //		{

    //			DataTable dataTable = OpenSheet(_connection, sheetName);
    //			if (dataTable == null) throw new Exception();
    //			T[] sheetData = LoadSheet<T>(dataTable);

    //			return sheetData;
    //		}
    //		catch (Exception e)
    //		{
    //			Debug.WriteLine(string.Format("Excel sheet load fail ({0}/{1})\r\n{2}",
    //										  excelPath, e.Message, e.StackTrace));
    //			_connection.Close();
    //			return null;
    //		}
    //	}

    //	static public DataTable OpenSheet(OdbcConnection _Connection, string _SheetName)
    //	{
    //		DataTable dataTable = new DataTable();

    //		try
    //		{
    //			OdbcCommand cmd = new OdbcCommand("SELECT * FROM [" + _SheetName + "$]", _Connection);
    //			OdbcDataReader dataReader = cmd.ExecuteReader();

    //			//while(dataReader.Read())
    //			//{
    //			//	Debug.WriteLine("내용:{0} : type : {1} : {2}", dataReader[0], dataReader.GetFieldType(0), dataReader.GetString(0) );
    //			//}

    //			dataTable.Load(dataReader);
    //			dataReader.Close();


    //		}
    //		catch (OdbcException)
    //		{
    //			return null;
    //		}

    //		return dataTable;
    //	}

    //	static public int GetTrimedRowCount(DataTable _Sheet)
    //	{
    //		int maxColumnCount = _Sheet.Columns.Count;
    //		int maxRowCount = _Sheet.Rows.Count;

    //		for (int nRow = 0; nRow < maxRowCount; ++nRow)
    //		{
    //			bool bEmpty = true;
    //			for (int nCol = 0; nCol < maxColumnCount; ++nCol)
    //			{
    //				object obj = _Sheet.Rows[nRow][nCol];

    //				if (!string.IsNullOrEmpty(obj.ToString()))
    //				{
    //					bEmpty = false;
    //				}
    //			}
    //			if (true == bEmpty)
    //			{
    //				return nRow;
    //			}
    //		}
    //		return maxRowCount;
    //	}

    //	static public T[] LoadSheet<T>(DataTable _Sheet) where T : LoadableData, new()
    //	{
    //		int maxColumnCount = _Sheet.Columns.Count;
    //		int maxRowCount = GetTrimedRowCount(_Sheet);// _Sheet.Rows.Count;

    //		string[] columnHead = GetColumnHead(_Sheet);

    //		T[] dataAry = new T[maxRowCount];

    //		string strOutputTotalError = string.Empty;

    //		for (int nRow = 0; nRow < maxRowCount; ++nRow)
    //		{
    //			T rowData = new T();

    //			for (int nCol = 0; nCol < maxColumnCount; ++nCol)
    //			{
    //				string typeName = columnHead[nCol];
    //				object obj = _Sheet.Rows[nRow][nCol];


    //				if (obj.GetType() == typeof(System.DBNull))
    //					continue;
    //				// 널 문자에 대한 예약문자;
    //				else if (obj.GetType() == typeof(string) && (string)obj == "-")
    //					continue;
    //				else if (string.IsNullOrEmpty(typeName))
    //					continue;
    //				// 사용하지 않을 Column에 대한 예약문자;
    //				else if (typeName[0] == '_')
    //					continue;

    //				rowData.SetData(columnHead[nCol], obj);
    //			}
    //			string strError = rowData.CheckCorrectData();
    //			if (string.IsNullOrEmpty(strError) == false)
    //			{
    //				string strOutputError = string.Format("LINE{0:D}\n{1}\n", nRow, strError);
    //				strOutputTotalError += strOutputError;
    //			}

    //			dataAry[nRow] = rowData;
    //		}

    //		if (string.IsNullOrEmpty(strOutputTotalError) == false)
    //		{
    //			string strTitle = string.Format("{0} Data Loading Error", typeof(T).ToString());
    //			Debug.WriteLine(strTitle);
    //			Debug.WriteLine(strOutputTotalError);
    //		}

    //		return dataAry;
    //	}

    //	static public T[] LoadSheetExtension<T>(DataTable _Sheet) where T : LoadableData, new()
    //	{
    //		const int baseObjOffset = 0;
    //		int maxColumnCount = _Sheet.Columns.Count;
    //		int maxRowCount = _Sheet.Rows.Count;
    //		string[] columnHead = GetColumnHead(_Sheet);
    //		TableParser parser = new TableParser();
    //		List<T> resultList = new List<T>();

    //		if (maxColumnCount < 1)
    //			throw new System.Exception("No Data");

    //		parser.ParseHead(columnHead);

    //		for (int nRow = 0; nRow < maxRowCount; ++nRow)
    //		{
    //			object baseObj = _Sheet.Rows[nRow][baseObjOffset];
    //			if (baseObj.ToString().StartsWith("//") == true)
    //				continue;

    //			T rowData = parser.GetRowData(resultList, baseObj);
    //			if (rowData == null)
    //				throw new System.Exception("[Row" + GetRowIndex(nRow).ToString() + ", Col" + GetColIndex(baseObjOffset) + "][" + baseObj.ToString() + "]");

    //			parser.ResetRowInfo();

    //			for (int nCol = 0; nCol < maxColumnCount; ++nCol)
    //			{
    //				object obj = _Sheet.Rows[nRow][nCol];
    //				if (parser.CanParse(nCol, obj) == true)
    //				{
    //					parser.ResetColumnInfo();

    //					if (parser.Parse(nCol, obj, rowData) == false)
    //						throw new System.Exception("[Row" + GetRowIndex(nRow).ToString() + ", Col" + GetColIndex(nCol) + "][" + obj.ToString() + "]");
    //				}
    //			}
    //		}

    //		return resultList.ToArray();
    //	}

    //	static public string[] GetColumnHead(DataTable _Sheet)
    //	{
    //		int maxColumnCount = _Sheet.Columns.Count;
    //		string[] columnHead = new string[maxColumnCount];

    //		for (int i = 0; i < maxColumnCount; ++i)
    //		{
    //			columnHead[i] = _Sheet.Columns[i].ColumnName;
    //		}

    //		return columnHead;
    //	}

    //	static int GetRowIndex(int _Row)
    //	{
    //		return _Row + 2;
    //	}

    //	static string GetColIndex(int _Col)
    //	{
    //		string[] ColIndex =
    //		{
    //			  "A", "B", "C", "D", "E",
    //			  "F", "G", "H", "I", "J",
    //			  "K", "L", "M", "N", "O",
    //			  "P", "Q", "R", "S", "T",
    //			  "U", "V", "W", "X", "Y",
    //			  "Z"
    //		  };

    //		int iCntSpell = 26;

    //		if (_Col < iCntSpell)
    //			return ColIndex[_Col];
    //		else if (_Col < iCntSpell * iCntSpell + iCntSpell)
    //			return ColIndex[_Col / iCntSpell - 1] + ColIndex[_Col % iCntSpell];
    //		else if (_Col < iCntSpell * iCntSpell * iCntSpell + iCntSpell)
    //		{
    //			int tmCol = _Col - iCntSpell;

    //			int tempIdx0 = tmCol / (iCntSpell * iCntSpell) - 1;
    //			int tempIdx1 = (tmCol % (iCntSpell * iCntSpell)) / iCntSpell;
    //			int tempIdx2 = (tmCol % (iCntSpell * iCntSpell)) % iCntSpell;

    //			return ColIndex[tempIdx0] + ColIndex[tempIdx1] + ColIndex[tempIdx2];
    //		}
    //		else
    //			return string.Empty;
    //	}
    //}




#endif

}
