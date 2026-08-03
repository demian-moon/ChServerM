using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace EcsServerLibM
{

	/// <summary>
	/// Struct 또는 클래스에 멤버변수 이름으로 set, 또는 get이 가능한 부모 클래스
	/// 상속하는 멤버 변수는 반드시 public으로 접근자가 설정 되어 있어야 함에 유의
	/// </summary>

	public abstract class LoadableDataInStructM
	{
		Type _thisType;

		public Type ThisType
		{
			get => _thisType ?? GetType(); set { _thisType = value; }
		}

		public bool GetData(string memberVarName, out object oVal)
		{
			FieldInfo fieldInfo = ThisType.GetField(memberVarName);

			try
			{
				if (fieldInfo.FieldType == typeof(DateTime))
				{
					DateTime dateVar = (DateTime)fieldInfo.GetValue(this);

					oVal = dateVar;
				}
				else if (fieldInfo.FieldType == typeof(TimeSpan))
				{
					var timeVar = (TimeSpan)fieldInfo.GetValue(this);
					oVal = string.Format($"{timeVar.Hours}h {timeVar.Minutes}m", timeVar);
				}
				else
				{
					oVal = fieldInfo.GetValue(this);
				}
			}
			catch
			{
				oVal = null;
				return false;

			}

			return true;
		}


		public virtual bool SetData(string memberVarName, object oVal)
		{
			FieldInfo fieldInfo = ThisType.GetField(memberVarName);
			if (fieldInfo == null)
			{
				string error = string.Format("SetData error : {0} Has Not {1}", ThisType.Name, memberVarName);
				Debug.WriteLine(error);
				return false;
			}
			else
			{
				try
				{
					if (fieldInfo.FieldType.IsEnum)
					{
						if (oVal.GetType() == typeof(string))
						{
							string strVal = (string)oVal;
							object convertVal = Enum.Parse(fieldInfo.FieldType, strVal);
							fieldInfo.SetValue(this, convertVal);
						}
						else
						{
							int nVal = Convert.ToInt32(oVal);
							object convertVal = Enum.ToObject(fieldInfo.FieldType, nVal);
							fieldInfo.SetValue(this, convertVal);
						}
					}
					else if (fieldInfo.FieldType == typeof(TimeSpan))
					{
						if (oVal?.GetType() == typeof(string))
						{
							if (TimeSpan.TryParseExact(oVal.ToString(), @"h\h\ mm\m", CultureInfo.InvariantCulture, out TimeSpan converVal) == true)
							{
								fieldInfo.SetValue(this, converVal);
							}
							else // 변환 불가
							{
								object convertVal = Convert.ChangeType(oVal, fieldInfo.FieldType);
								fieldInfo.SetValue(this, convertVal);
							}
						}
					}
					else if (fieldInfo.FieldType == typeof(DateTime))
					{
						if (oVal != null) // 값이 null이면 DateTime.MinValue값을 갖게 됨 (structure라 set을 안해도)
						{

							if (oVal.GetType() == typeof(DateTime))
							{
								object convertVal = Convert.ChangeType(oVal, fieldInfo.FieldType);
								fieldInfo.SetValue(this, convertVal);
							}
							else
							{
								fieldInfo.SetValue(this, DateTime.MinValue.AddDays((double)oVal));
							}
						}
						else
						{
							fieldInfo.SetValue(this, null);
						}
					}
					else
					{
						if (oVal != null)
						{
							// 클래스에 선언되어(FieldType) 있는 멤버 타입으로 엑셀의 데이터 값을 변환 함 
							object convertVal = Convert.ChangeType(oVal, fieldInfo.FieldType);
							fieldInfo.SetValue(this, convertVal);
						}

					}
					return true;
				}
				catch
				{
					string error = string.Format("SetData error : {0} {1} {2}", ThisType.Name, memberVarName, oVal?.ToString());
					Debug.WriteLine(error);

					return false;
				}
			}
		}

		public bool SetData(string[] arrMemVarName, object[] arrObjectData)
		{

			for (int i = 0; i < arrMemVarName.Length; i++)
			{
				if (SetData(arrMemVarName[i], arrObjectData[i]) == false)
					return false;
			}

			return true;
		}


		static public bool CheckCorrectDataField(Type thisType, string[] fieldNames)
		{
			FieldInfo fieldInfo;
			for (int i = 0; i < fieldNames.Length; i++)
			{
				fieldInfo = thisType.GetField(fieldNames[i]);
				if (fieldInfo == null)
				{
					return false;
				}
			}

			return true;
		}

	}
}
