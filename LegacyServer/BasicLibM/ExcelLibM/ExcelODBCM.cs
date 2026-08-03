using System.Collections;
using System;
using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;




namespace ExcelODBCM
{
	static public class ExcelM
    {

		static public OdbcConnection _connection = null;
 

		static public OdbcConnection Open(string _ExcelPath)
        {
            if (System.IO.File.Exists(_ExcelPath) == false)
            {
                return null;
            }

            //string connStr = "Driver={Microsoft Excel Driver (*.xls)}; DriverId=790; Dbq=" + _ExcelPath + ";";

			string connStr = "Driver={Microsoft Excel Driver (*.xls, *.xlsx, *.xlsm, *.xlsb)};Extended Properties='Excel 8.0;HDR=YES';DBQ=" +_ExcelPath + ";";


			OdbcConnection connection = new OdbcConnection(connStr);			
			connection.Open();

            return connection;
        }

		static public void Close()
        {
			if (_connection != null)
			{
				_connection.Close();
				_connection = null;
			}
        }

        static public void Close(OdbcConnection _Connection)
        {
            _Connection.Close();
        }

		static public T[] ReadSheet<T>(string excelPath, string sheetName) where T : LoadableData, new()
        {
			if (_connection == null)	// close 하면 null이 된다. 
			{
				_connection = Open(excelPath);
				if (_connection == null)
				{
					Debug.WriteLine("Excel load fail (" + excelPath + ")");
					return null;
				}
			}

			try
			{

				DataTable dataTable = OpenSheet(_connection, sheetName);
				if (dataTable == null) throw new Exception();
				T[] sheetData = LoadSheet<T>(dataTable);

				return sheetData;
			}
			catch (Exception e)
			{
				Debug.WriteLine(string.Format("Excel sheet load fail ({0}/{1})\r\n{2}",
											  excelPath, e.Message, e.StackTrace));
				_connection.Close();				
				return null;
			}
		}

		static public DataTable OpenSheet(OdbcConnection _Connection, string _SheetName)
        {
            DataTable dataTable = new DataTable();

            try
            {
                OdbcCommand cmd = new OdbcCommand("SELECT * FROM [" + _SheetName + "$]", _Connection);
                OdbcDataReader dataReader = cmd.ExecuteReader();

				//while(dataReader.Read())
				//{
				//	Debug.WriteLine("내용:{0} : type : {1} : {2}", dataReader[0], dataReader.GetFieldType(0), dataReader.GetString(0) );
				//}
				
                dataTable.Load(dataReader);
                dataReader.Close();

				
            }
            catch (OdbcException)
            {
                return null;
            }

            return dataTable;
        }

        static public int GetTrimedRowCount(DataTable _Sheet)
        {
            int maxColumnCount = _Sheet.Columns.Count;
            int maxRowCount = _Sheet.Rows.Count;

            for (int nRow = 0; nRow < maxRowCount; ++nRow)
            {
                bool bEmpty = true;
                for (int nCol = 0; nCol < maxColumnCount; ++nCol)
                {
                    object obj = _Sheet.Rows[nRow][nCol];

                    if (!string.IsNullOrEmpty(obj.ToString()))
                    {
                        bEmpty = false;
                    }
                }
                if (true == bEmpty)
                {
                    return nRow;
                }
            }
            return maxRowCount;
        }

        static public T[] LoadSheet<T>(DataTable _Sheet) where T : LoadableData, new()
        {
            int maxColumnCount = _Sheet.Columns.Count;
            int maxRowCount = GetTrimedRowCount(_Sheet);// _Sheet.Rows.Count;

            string[] columnHead = GetColumnHead(_Sheet);

            T[] dataAry = new T[maxRowCount];

            string strOutputTotalError = string.Empty;

            for (int nRow = 0; nRow < maxRowCount; ++nRow)
            {
                T rowData = new T();

                for (int nCol = 0; nCol < maxColumnCount; ++nCol)
                {
                    string typeName = columnHead[nCol];
                    object obj = _Sheet.Rows[nRow][nCol];
					

					if (obj.GetType() == typeof(System.DBNull))
						continue;					
					// 널 문자에 대한 예약문자;
					else if (obj.GetType() == typeof(string) && (string)obj == "-")
						continue;
					else if (string.IsNullOrEmpty(typeName))
						continue;
					// 사용하지 않을 Column에 대한 예약문자;
					else if (typeName[0] == '_')
						continue;

                    rowData.SetData(columnHead[nCol], obj);
                }
                string strError = rowData.CheckCorrectData();
                if (string.IsNullOrEmpty(strError) == false)
                {
                    string strOutputError = string.Format("LINE{0:D}\n{1}\n", nRow, strError);
                    strOutputTotalError += strOutputError;
                }
				
                dataAry[nRow] = rowData;
            }

            if (string.IsNullOrEmpty(strOutputTotalError) == false)
            {
                string strTitle = string.Format("{0} Data Loading Error", typeof(T).ToString());
                Debug.WriteLine(strTitle);
                Debug.WriteLine(strOutputTotalError);
            }

            return dataAry;
        }

        static public T[] LoadSheetExtension<T>(DataTable _Sheet) where T : LoadableData, new()
        {
            const int baseObjOffset = 0;
            int maxColumnCount = _Sheet.Columns.Count;
            int maxRowCount = _Sheet.Rows.Count;
            string[] columnHead = GetColumnHead(_Sheet);
            TableParser parser = new TableParser();
            List<T> resultList = new List<T>();

            if (maxColumnCount < 1)
                throw new System.Exception("No Data");

            parser.ParseHead(columnHead);

            for (int nRow = 0; nRow < maxRowCount; ++nRow)
            {
                object baseObj = _Sheet.Rows[nRow][baseObjOffset];
                if (baseObj.ToString().StartsWith("//") == true)
                    continue;

                T rowData = parser.GetRowData(resultList, baseObj);
                if (rowData == null)
                    throw new System.Exception("[Row" + GetRowIndex(nRow).ToString() + ", Col" + GetColIndex(baseObjOffset) + "][" + baseObj.ToString() + "]");

                parser.ResetRowInfo();

                for (int nCol = 0; nCol < maxColumnCount; ++nCol)
                {
                    object obj = _Sheet.Rows[nRow][nCol];
                    if (parser.CanParse(nCol, obj) == true)
                    {
                        parser.ResetColumnInfo();

                        if (parser.Parse(nCol, obj, rowData) == false)
                            throw new System.Exception("[Row" + GetRowIndex(nRow).ToString() + ", Col" + GetColIndex(nCol) + "][" + obj.ToString() + "]");
                    }
                }
            }

            return resultList.ToArray();
        }

        static public string[] GetColumnHead(DataTable _Sheet)
        {
            int maxColumnCount = _Sheet.Columns.Count;
            string[] columnHead = new string[maxColumnCount];

            for (int i = 0; i < maxColumnCount; ++i)
            {
                columnHead[i] = _Sheet.Columns[i].ColumnName;
            }

            return columnHead;
        }

        static int GetRowIndex(int _Row)
        {
            return _Row + 2;
        }

        static string GetColIndex(int _Col)
        {
            string[] ColIndex =
            {
            "A", "B", "C", "D", "E",
            "F", "G", "H", "I", "J",
            "K", "L", "M", "N", "O",
            "P", "Q", "R", "S", "T",
            "U", "V", "W", "X", "Y",
            "Z"
        };

            int iCntSpell = 26;

            if (_Col < iCntSpell)
                return ColIndex[_Col];
            else if (_Col < iCntSpell * iCntSpell + iCntSpell)
                return ColIndex[_Col / iCntSpell - 1] + ColIndex[_Col % iCntSpell];
            else if (_Col < iCntSpell * iCntSpell * iCntSpell + iCntSpell)
            {
                int tmCol = _Col - iCntSpell;

                int tempIdx0 = tmCol / (iCntSpell * iCntSpell) - 1;
                int tempIdx1 = (tmCol % (iCntSpell * iCntSpell)) / iCntSpell;
                int tempIdx2 = (tmCol % (iCntSpell * iCntSpell)) % iCntSpell;

                return ColIndex[tempIdx0] + ColIndex[tempIdx1] + ColIndex[tempIdx2];
            }
            else
                return string.Empty;
        }
    }


	/// <summary>
	/// ///////////////////////////////////////
	/// </summary>

	public abstract class LoadableData
	{
		protected bool SplitRangeValue(object _Val, float _MinVal, float _MaxVal, out float _ResultMinVal, out float _ResultMaxVal)
		{

			try
			{
				string strVal = (string)_Val;

				int tokIndex = strVal.IndexOf('~');
				if (tokIndex >= 0)
				{
					string strMinVal = strVal.Substring(0, tokIndex);
					string strMaxVal = strVal.Substring(tokIndex + 1, strVal.Length - tokIndex - 1);
					float fMinVal = string.IsNullOrEmpty(strMinVal) ? _MinVal : float.Parse(strMinVal);
					float fMaxVal = string.IsNullOrEmpty(strMaxVal) ? _MaxVal : float.Parse(strMaxVal);
					_ResultMinVal = fMinVal;
					_ResultMaxVal = fMaxVal;
				}
				else
				{
					float fVal = float.Parse(strVal);
					_ResultMinVal = fVal;
					_ResultMaxVal = fVal;
				}

				return true;
			}
			catch
			{
				_ResultMinVal = _MinVal;
				_ResultMaxVal = _MaxVal;

				Type thisType = GetType();
				string error = string.Format("SetData error : {0} {1}", thisType.Name, _Val.ToString());
				Debug.WriteLine(error);
				return false;
			}
		}

		protected bool SetDataArray<T>(string _Name, object _Val)
		{
			Type thisType = GetType();
			FieldInfo fieldInfo = thisType.GetField(_Name);
			if (fieldInfo == null)
			{
				string error = string.Format("SetData error : {0} Has Not {1}", thisType.Name, _Name);
				Debug.WriteLine(error);
				return false;
			}
			else
			{
				try
				{
					if (_Val.GetType() == typeof(string))
					{
						string strVal = (string)_Val;
						String[] strAry = strVal.Split(',', ' ');
						T[] arrResult = new T[strAry.Length];
						for (int i = 0; i < strAry.Length; i++)
						{
							object convertObj = Convert.ChangeType(strAry[i], typeof(T));
							arrResult[i] = (T)convertObj;
						}

						fieldInfo.SetValue(this, arrResult);
					}
					else
					{
						return false;
					}
					return true;
				}
				catch
				{
					string error = string.Format("SetData error : {0} {1} {2}", thisType.Name, _Name, _Val.ToString());
					Debug.WriteLine(error);
					return false;
				}
			}


		}

		public virtual bool SetData(string _Name, object _Val)
		{
			Type thisType = GetType();
			FieldInfo fieldInfo = thisType.GetField(_Name);			
			if (fieldInfo == null)
			{
				string error = string.Format("SetData error : {0} Has Not {1}", thisType.Name, _Name);
				Debug.WriteLine(error);
				return false;
			}
			else
			{
				try
				{
					if (fieldInfo.FieldType.IsEnum)
					{
						if (_Val.GetType() == typeof(string))
						{
							string strVal = (string)_Val;
							object convertVal = Enum.Parse(fieldInfo.FieldType, strVal);
							fieldInfo.SetValue(this, convertVal);
						}
						else
						{
							int nVal = Convert.ToInt32(_Val);
							object convertVal = Enum.ToObject(fieldInfo.FieldType, nVal);
							fieldInfo.SetValue(this, convertVal);
						}
					}
					else
					{
						object convertVal = Convert.ChangeType(_Val, fieldInfo.FieldType);
						fieldInfo.SetValue(this, convertVal);
					}
					return true;
				}
				catch
				{
					string error = string.Format("SetData error : {0} {1} {2}", thisType.Name, _Name, _Val.ToString());
					Debug.WriteLine(error);

					return false;
				}
			}
		}

		public virtual string CheckCorrectData()
		{
			return string.Empty;
		}
	}




	/// <summary>
	/// ////////////////////////////////////////////////////////////////////////////////////////////////////
	/// </summary>


	public class ClassMemberInfo
	{
		public string Name = string.Empty;
		public int Index = -1;

		public bool HasIndex()
		{
			return Index >= 0;
		}

		public void Parse(string _MemberName)
		{
			string indexPattern = "\\([0-9]+\\)$";      // "\\[[0-9]+\\]"를 사용하고 싶은데 excel이 맘대로 '[' -> '(', ']' -> ')'로 치환해 버린다;
			string indexValuePattern = "[0-9]+";

			Name = _MemberName;

			// Index Pattern Check
			Match match = Regex.Match(Name, indexPattern);
			string matchString = match.ToString();
			if (string.IsNullOrEmpty(matchString) == true)
				return;

			Match subMatch = Regex.Match(matchString, indexValuePattern);
			string subMatchString = subMatch.ToString();
			Index = int.Parse(subMatchString);

			Name = Regex.Replace(Name, indexPattern, string.Empty);
		}
	}


	//"a(1).b(2).c(3)(U)
	public class TableColumnInfo
	{
		static private string uniquePattern = "\\(U\\)$";           // "\\[U]\\]"를 사용하고 싶은데 excel이 맘대로 '[' -> '(', ']' -> ')'로 치환해 버린다;
		static private char[] separator = new char[] { '.', '#' };  // '.'만 사용하고 싶은데 excel이 맘대로 '.' -> '#'로 치환해 버린다;

		public ClassMemberInfo[] ClassMemberInfoArray = null;

		public void Parse(string _ColumnIndexHead, ref bool _IsUnique)
		{
			// Unique Pattern Check
			_IsUnique = Regex.IsMatch(_ColumnIndexHead, uniquePattern);
			if (_IsUnique == true)
				_ColumnIndexHead = Regex.Replace(_ColumnIndexHead, uniquePattern, string.Empty);

			string[] tokenArray = _ColumnIndexHead.Split(separator);
			ClassMemberInfoArray = new ClassMemberInfo[tokenArray.Length];

			for (int i = 0; i < tokenArray.Length; i++)
			{
				ClassMemberInfoArray[i] = new ClassMemberInfo();
				ClassMemberInfoArray[i].Parse(tokenArray[i]);
			}
		}
	}


	// a[0], true  
	public class TableHeadInfo
	{
		private Dictionary<string, bool> m_ClassMemberNamePool = null;
		public TableColumnInfo[] ColumnInfoArray = null;
		public int UniqueIndex = -1;

		public bool IsUniqueRow()
		{
			return UniqueIndex >= 0;
		}

		public void Parse(string[] _ColumnIndexHead)
		{
			m_ClassMemberNamePool = new Dictionary<string, bool>();
			ColumnInfoArray = new TableColumnInfo[_ColumnIndexHead.Length];
			UniqueIndex = -1;

			for (int i = 0; i < _ColumnIndexHead.Length; i++)
			{
				bool isUnique = false;

				ColumnInfoArray[i] = new TableColumnInfo();
				ColumnInfoArray[i].Parse(_ColumnIndexHead[i], ref isUnique);
				AddClassMemberName(i, isUnique);
			}
		}

		public bool IsUniqueClassMember(string _MemberNameKey)
		{
			return m_ClassMemberNamePool.ContainsKey(_MemberNameKey) == true && m_ClassMemberNamePool[_MemberNameKey] == true;
		}

		private void AddClassMemberName(int _ColumnIndex, bool _IsUnique)
		{
			ClassMemberInfo[] classMemberInfoArray = ColumnInfoArray[_ColumnIndex].ClassMemberInfoArray;

			if (classMemberInfoArray.Length == 1 && _IsUnique == true)
				UniqueIndex = _ColumnIndex;

			string classMemberName = string.Empty;

			for (int i = 0; i < classMemberInfoArray.Length; i++)
			{
				string name = classMemberInfoArray[i].Name;

				if (classMemberInfoArray[i].HasIndex() == true)
					name += "[" + classMemberInfoArray[i].Index.ToString() + "]";

				if (string.IsNullOrEmpty(classMemberName) == true)
					classMemberName = name;
				else
					classMemberName = classMemberName + "#" + name;

				if (m_ClassMemberNamePool.ContainsKey(classMemberName) == false)
				{
					if (IsUniqueRow() == true && _IsUnique == true)
						m_ClassMemberNamePool.Add(classMemberName, true);
					else
						m_ClassMemberNamePool.Add(classMemberName, false);
				}
			}
		}
	}

	/// <summary>
	/// ////////////////////////////////////////////////////////////////////////////////
	/// </summary>


	public class TableParser
	{
		private TableHeadInfo m_HeadInfo = null;
		private Dictionary<string, object> m_UniqueObjectPool = null;
		private Dictionary<string, object> m_RowObjectPool = null;
		private Dictionary<string, string> m_MemberValuePool = null;

		public string UniqueRowKey = string.Empty;
		public string MemberNameKey = string.Empty;
		public string ValueNameKey = string.Empty;

		private void UpdateUniqueRowKey(string _Value, int _Column)
		{
			if (m_HeadInfo.IsUniqueRow() == true && m_HeadInfo.UniqueIndex == _Column)
				UniqueRowKey = _Value;
		}

		private void UpdateMemberNameKey(string _MemberName, bool _HasIndex, int _Index)
		{
			string name = _MemberName;
			if (_HasIndex == true)
			{
				name += "[" + _Index.ToString() + "]";
			}

			if (string.IsNullOrEmpty(MemberNameKey) == true)
				MemberNameKey = name;
			else
				MemberNameKey = MemberNameKey + "#" + name;
		}

		private void UpdateValueNameKey(string _MemberName, string _Value, bool _HasIndex, int _Index)
		{
			string memberName = _MemberName;
			if (_HasIndex == true)
			{
				memberName += "[" + _Index.ToString() + "]";
			}
			string name = memberName + "." + _Value;

			if (m_MemberValuePool.ContainsKey(memberName) == false)
				m_MemberValuePool.Add(memberName, name);
			else
				name = m_MemberValuePool[memberName];

			if (string.IsNullOrEmpty(ValueNameKey) == true)
			{
				if (m_HeadInfo.IsUniqueRow() == true)
					ValueNameKey = UniqueRowKey + "#" + name;
				else
					ValueNameKey = name;
			}
			else
				ValueNameKey = ValueNameKey + "#" + name;
		}

		public void ParseHead(string[] _ColumnIndexHead)
		{
			m_UniqueObjectPool = new Dictionary<string, object>();
			m_HeadInfo = new TableHeadInfo();
			m_HeadInfo.Parse(_ColumnIndexHead);
		}

		public void ResetRowInfo()
		{
			m_MemberValuePool = new Dictionary<string, string>();
			m_RowObjectPool = new Dictionary<string, object>();
			UniqueRowKey = string.Empty;
			MemberNameKey = string.Empty;
		}

		public void ResetColumnInfo()
		{
			MemberNameKey = string.Empty;
			ValueNameKey = string.Empty;
		}

		public T GetRowData<T>(List<T> _ResultList, object _Object) where T : LoadableData, new()
		{
			string key = string.Empty;

			if (m_HeadInfo.IsUniqueRow() == true)
			{
				key = _Object.ToString();

				if (string.IsNullOrEmpty(key) == true)
					return default(T);

				if (m_UniqueObjectPool.ContainsKey(key) == true)
					return (T)m_UniqueObjectPool[key];
			}

			T rowData = new T();
			_ResultList.Add(rowData);

			if (m_HeadInfo.IsUniqueRow() == true)
				m_UniqueObjectPool.Add(key, rowData);

			return rowData;
		}

		public bool CanParse(int _ColumnIndex, object _Object)
		{
			if (_Object.GetType() == typeof(System.DBNull))
				return false;

			// 널 문자에 대한 예약문자;
			if (_Object.GetType() == typeof(string) && (string)_Object == "-")
				return false;

			if (string.IsNullOrEmpty(m_HeadInfo.ColumnInfoArray[_ColumnIndex].ClassMemberInfoArray[0].Name))
				return false;

			// 사용하지 않을 Column에 대한 예약문자;
			if (m_HeadInfo.ColumnInfoArray[_ColumnIndex].ClassMemberInfoArray[0].Name[0] == '_')
				return false;

			return true;
		}

		public bool Parse(int _ColumnIndex, object _Object, object _RowData)
		{
			return Parse(_ColumnIndex, _Object, _RowData, 0);
		}

		private bool Parse(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex)
		{
			if (_TargetObject == null)
				return false;

			TableColumnInfo columnInfo = m_HeadInfo.ColumnInfoArray[_ColumnIndex];
			ClassMemberInfo classMemberInfo = columnInfo.ClassMemberInfoArray[_ClassMemberIndex];

			bool isLastMember = _ClassMemberIndex == columnInfo.ClassMemberInfoArray.Length - 1;

			UpdateUniqueRowKey(_Object.ToString(), _ColumnIndex);
			UpdateMemberNameKey(classMemberInfo.Name, classMemberInfo.HasIndex(), classMemberInfo.Index);
			UpdateValueNameKey(classMemberInfo.Name, _Object.ToString(), classMemberInfo.HasIndex(), classMemberInfo.Index);

			if (isLastMember)
				return SetData(_ColumnIndex, _Object, _TargetObject, _ClassMemberIndex);
			else
				return Parse(_ColumnIndex, _Object, GetNextTargetObject(_ColumnIndex, _Object, _TargetObject, _ClassMemberIndex), _ClassMemberIndex + 1);
		}

		private object DoMethod(object _TargetObject, string _MethodName, object[] _ParamArray)
		{
			MethodInfo methodInfo = _TargetObject.GetType().GetMethod(_MethodName);
			if (methodInfo == null)
				return null;

			return methodInfo.Invoke(_TargetObject, _ParamArray);
		}

		private object DoMethod(object _TargetObject, string _MethodName, Type[] _TypeArray, object[] _ParamArray)
		{
			MethodInfo methodInfo = _TargetObject.GetType().GetMethod(_MethodName, _TypeArray);
			if (methodInfo == null)
				return null;

			return methodInfo.Invoke(_TargetObject, _ParamArray);
		}

		private object GetNewObject(Type _FieldType, object _Object)
		{
			if (_FieldType.IsEnum == true)
				return Enum.Parse(_FieldType, _Object.ToString());

			return Convert.ChangeType(_Object, _FieldType);
		}

		private bool SetData(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex)
		{
			TableColumnInfo columnInfo = m_HeadInfo.ColumnInfoArray[_ColumnIndex];
			ClassMemberInfo classMemberInfo = columnInfo.ClassMemberInfoArray[_ClassMemberIndex];
			FieldInfo fieldInfo = _TargetObject.GetType().GetField(classMemberInfo.Name);
			if (fieldInfo == null)
				return false;
			object fieldObject = fieldInfo.GetValue(_TargetObject);

			if (fieldInfo.FieldType.IsArray == true)
			{
				Type elementType = fieldInfo.FieldType.GetElementType();
				object newObject = GetNewObject(elementType, _Object);

				AddElementToArray(fieldInfo, fieldObject, _TargetObject, newObject, classMemberInfo.HasIndex(), classMemberInfo.Index);
			}
			else if (fieldInfo.FieldType.IsGenericType == true)
			{
				if (fieldObject == null)
				{
					fieldObject = Activator.CreateInstance(fieldInfo.FieldType);
					fieldInfo.SetValue(_TargetObject, fieldObject);
				}

				Type elementType = fieldInfo.FieldType.GetGenericArguments()[0];

				object newObject = GetNewObject(elementType, _Object);
				DoMethod(fieldObject, "Add", new object[1] { newObject });
			}
			else
			{
				object newObject = GetNewObject(fieldInfo.FieldType, _Object);
				fieldInfo.SetValue(_TargetObject, newObject);
			}

			return true;
		}

		private object GetNextTargetObject(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex)
		{
			TableColumnInfo columnInfo = m_HeadInfo.ColumnInfoArray[_ColumnIndex];
			ClassMemberInfo classMemberInfo = columnInfo.ClassMemberInfoArray[_ClassMemberIndex];

			FieldInfo fieldInfo = _TargetObject.GetType().GetField(classMemberInfo.Name);
			if (fieldInfo == null)
				return null;

			object fieldObject = fieldInfo.GetValue(_TargetObject);

			if (fieldInfo.FieldType.IsArray == true)
			{
				bool isExist = true;
				object newObject = GetElementObject(_ColumnIndex, _Object, fieldObject, _ClassMemberIndex, fieldInfo, ref isExist);
				if (newObject == null)
					return null;

				if (isExist == true)
					return newObject;

				AddElementToArray(fieldInfo, fieldObject, _TargetObject, newObject, classMemberInfo.HasIndex(), classMemberInfo.Index);

				return newObject;
			}
			else if (fieldInfo.FieldType.IsGenericType == true)
			{
				bool isExist = true;
				object newObject = GetElementObject(_ColumnIndex, _Object, fieldObject, _ClassMemberIndex, fieldInfo, ref isExist);
				if (newObject == null)
					return null;

				if (isExist == true)
					return newObject;

				if (fieldObject == null)
				{
					fieldObject = Activator.CreateInstance(fieldInfo.FieldType);
					fieldInfo.SetValue(_TargetObject, fieldObject);
					fieldObject = fieldInfo.GetValue(_TargetObject);
				}

				DoMethod(fieldObject, "Add", new object[1] { newObject });
				return newObject;
			}
			else
			{
				if (fieldObject == null)
				{
					fieldObject = Activator.CreateInstance(fieldInfo.FieldType);
					fieldInfo.SetValue(_TargetObject, fieldObject);
				}

				return fieldObject;
			}
		}

		private void AddElementToArray(FieldInfo _ArrayFieldInfo, object _ArrayObject, object _TargetObject, object _NewObject, bool _HasIndex, int _Index)
		{
			Type elementType = _ArrayFieldInfo.FieldType.GetElementType();
			Array newArray = null;
			int index = 0;
			int size = 0;
			int newSize = 0;

			if (_ArrayObject == null)
			{
				if (_HasIndex == true)
				{
					index = _Index;
					newSize = index + 1;
				}
				else
				{
					index = 0;
					newSize = 1;
				}

				newArray = Array.CreateInstance(elementType, newSize);
				_ArrayFieldInfo.SetValue(_TargetObject, newArray);
				_ArrayObject = _ArrayFieldInfo.GetValue(_TargetObject);
			}
			else
			{
				size = (int)DoMethod(_ArrayObject, "GetLength", new object[1] { 0 });
				if (_HasIndex == true)
				{
					index = _Index;
					if (index < size)
						newSize = size;
					else
						newSize = index + 1;
				}
				else
				{
					index = size;
					newSize = size + 1;
				}

				if (newSize != size)
				{
					newArray = Array.CreateInstance(elementType, newSize);
					Array.Copy((Array)_ArrayObject, newArray, size);
					_ArrayFieldInfo.SetValue(_TargetObject, newArray);
					_ArrayObject = _ArrayFieldInfo.GetValue(_TargetObject);
				}
			}

			DoMethod(_ArrayObject, "SetValue", new Type[] { typeof(object), typeof(int) }, new object[2] { _NewObject, index });
		}

		private object GetElementObject(int _ColumnIndex, object _Object, object _TargetObject, int _ClassMemberIndex, FieldInfo _FieldInfo, ref bool _IsExist)
		{
			object elementObject = null;

			if (m_HeadInfo.IsUniqueClassMember(MemberNameKey) == true)
			{
				if (m_UniqueObjectPool.ContainsKey(ValueNameKey) == true)
					elementObject = m_UniqueObjectPool[ValueNameKey];
			}

			if (elementObject == null)
			{
				if (m_RowObjectPool.ContainsKey(ValueNameKey) == true)
					elementObject = m_RowObjectPool[ValueNameKey];
			}

			if (elementObject == null)
			{
				_IsExist = false;
				Type elementType;
				// 현재 버전은 Array와 List만 처리할 수 있다;
				if (_FieldInfo.FieldType.IsArray == true)
					elementType = _FieldInfo.FieldType.GetElementType();
				else if (_FieldInfo.FieldType.GetGenericTypeDefinition() == typeof(List<>))
					elementType = _FieldInfo.FieldType.GetGenericArguments()[0];
				else
					return null;

				if (elementType.IsArray == true)
					elementObject = Array.CreateInstance(elementType, 0);
				else
					elementObject = Activator.CreateInstance(elementType);

				if (m_HeadInfo.IsUniqueClassMember(MemberNameKey) == true)
					m_UniqueObjectPool.Add(ValueNameKey, elementObject);

				m_RowObjectPool.Add(ValueNameKey, elementObject);
			}

			return elementObject;
		}
	}





}
