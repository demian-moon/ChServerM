using UnityEngine;
using System.Collections.Generic;

/* CsvParser Class 설명
 * 
 * Csv 파일을 읽어와서 rows에 컬럼명, 값 Dictionary 타입으로 넣어주는 클래스 
 * 헤더는 첫줄에 반드시 있어야 한다 - 헤더명이 키값이 됨
 * 중간에 빈줄이 있어도 된다
 * 
 * ex) 파일구조 (첫번째 줄은 헤더)
 * ID,Name,ElementType,Grade,MaxHP,Attack,Armor,Move,Critical, .....
 * 1001,KAI,FIRE,4,100,110,50,5,0.1,7,10,5,4,,3,kai,,,1,3
 * 1002,BABY GUMI,WATER,1,30,10,5,3,0.1,7,3,2,1,,1.5,baby_gumi,,,0,0
 * */

public class CsvParser
{
	private string[] _columnNames;
	private List<Dictionary<string, string>> _rows = new List<Dictionary<string, string>>();
	
	public List<Dictionary<string, string>> rows { get { return _rows; } }
	
	public CsvParser(string csvFileName)
	{
		Parse(csvFileName);
	}
	
	~CsvParser()
	{
		Cleanup();	
	}

	/* 모두 지운다 */
	public void Cleanup()
	{
		for (int i = 0; i < _rows.Count; ++i)
		{
			_rows[i].Clear();
			_rows[i] = null;
		}
		
		_rows = null;
	}
	
	public static string GetStringValue(Dictionary<string, string> row, string columnName, string defaultValue = "")
	{
		string stringValue = defaultValue;
		string columnNameUpper = columnName.ToUpper();
		
		if (row != null && row.ContainsKey(columnNameUpper))
		{
			stringValue = row[columnNameUpper];
		}
		
		return stringValue;
	}
	
	public static int GetIntValue(Dictionary<string, string> row, string columnName, int defaultValue = 0)
	{
		return int.Parse(GetStringValue(row, columnName, defaultValue.ToString()));
	}
	
	public static uint GetUintValue(Dictionary<string, string> row, string columnName, uint defaultValue = 0)
	{
		return uint.Parse(GetStringValue(row, columnName, defaultValue.ToString()));
	}
	
	public static float GetFloatValue(Dictionary<string, string> row, string columnName, float defaultValue = 0)
	{
		return float.Parse(GetStringValue(row, columnName, defaultValue.ToString()));
	}
	
	public static bool GetBooleanValue(Dictionary<string, string> row, string columnName)
	{
		return GetIntValue(row, columnName) != 0;
	}
	
	public static List<string> GetStringList(Dictionary<string, string> row, string columnName)
	{
		List<string> stringList = new List<string>();
		string columnNameUpper = columnName.ToUpper();
		
		if (row != null && row.ContainsKey(columnNameUpper))
		{
			string[] splits = GetStringValue(row, columnName).Split(';');
			
			foreach (string split in splits)
			{
				if (split.Length > 0)
				{
					stringList.Add(split);
				}
			}
		}
		
		return stringList;
	}
	
	public static List<int> GetIntList(Dictionary<string, string> row, string columnName)
	{
		List<int> intList = new List<int>();
		string columnNameUpper = columnName.ToUpper();
		
		if (row != null && row.ContainsKey(columnNameUpper))
		{
			string[] splits = GetStringValue(row, columnName).Split(';');
			
			foreach (string split in splits)
			{
				if (split.Length > 0)
				{
					int value;
					
					if (int.TryParse(split, out value))
				    {
						intList.Add(value);
				    }
				}
			}
		}
		
		return intList;
	}
	
	private void Parse(string csvPathFile)
	{
		TextAsset textAsset = (TextAsset)Resources.Load(csvPathFile, typeof(TextAsset));
		
		if (textAsset == null)
		{
			Debug.LogError(csvPathFile + " table loading is failed.");
			
			return;
		}
		
		string[] rowList = textAsset.text.Split('\n');
		
		if (rowList.Length > 1)
		{
			ParseHeader(rowList[0].Replace("\r", ""));
			
			for (int i = 1; i < rowList.Length; ++i)
			{
				ParseRow(rowList[i].Replace("\r", ""));
			}
		}
	}
	
	private void ParseHeader(string strHeader)
	{
		_columnNames = strHeader.Split(',');
		
		for (int i = 0; i < _columnNames.Length; ++i)
		{
			_columnNames[i] = _columnNames[i].Trim().ToUpper();
		}
	}
	
	private void ParseRow(string strRow)
	{
		string[] columns = strRow.Split(',');
		
		if (columns.Length == _columnNames.Length)
		{
			Dictionary<string, string> newRow = new Dictionary<string, string>();
			
			for (int i = 0; i < columns.Length; ++i)
			{
				if (columns[i].Length > 0)
				{
					newRow[_columnNames[i]] = columns[i];
				}
				else
				{
					Debug.LogError("Error : csv file header's Names must have characters more than 1");
				}
			}
			
			_rows.Add(newRow);
		}
	}
}
