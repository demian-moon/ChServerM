using System;
using System.Diagnostics;
using System.Text;

namespace EcsServerLibM
{

	public class StringBuilderM
	{
		StringBuilder _sb = new StringBuilder();
		public StringBuilderM(string str)
		{
			_sb.Append(str);
		}

		public StringBuilderM() { }

		override public string ToString()
		{
			return _sb.ToString();
		}

		public void Append(string str)
		{
			_sb.Append(str);
		}

		public void Append(StringBuilder sb)
		{
			_sb.Append(sb);
		}

		public static StringBuilderM operator +(StringBuilderM a, StringBuilderM b)
		{
			a._sb.Append(b._sb);
			return a;
		}

		public void AppendLine(string str)
		{
			_sb.AppendLine(str);
		}

		public void AppendFormat(string format, params object[] args)
		{
			_sb.AppendFormat(format, args);
		}

		public void Write(bool bConsole = false)
		{
			if (bConsole)
			{
				Console.Write(_sb.ToString());
				return;
			}

			Debug.Write(_sb);
		}

		public void Clear()
		{
			_sb.Clear();
		}

		public char this[int i]
		{
			get { return _sb[i]; }
		}

	}

}
