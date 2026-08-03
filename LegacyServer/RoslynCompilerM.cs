using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace EcsServerLibM
{
	public class RoslynCompilerM
	{
		//private static readonly ServiceCollection _serviceCollection = new ServiceCollection();
		//private static ServiceProvider _serviceProvider;

		/// <summary>
		/// 컴파일 할 때 쓰이는 레퍼런스 dll
		/// </summary>
		private static readonly List<MetadataReference> _references = AppDomain.CurrentDomain.GetAssemblies()
			.Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
			.Select(a => MetadataReference.CreateFromFile(a.Location))
			.Cast<MetadataReference>()
			.ToList();

		// 소스 코드 파일 경로를 받아 컴파일
		public static Assembly CompileFiles<T>(string[] sourceFilePaths, string errorLogFileName = "compileLog.txt")
		{
			List<SyntaxTree> syntaxTrees = sourceFilePaths
				.Select(ParseSyntaxTreeFromFile)
				.ToList();

			CSharpCompilation compilation = CSharpCompilation.Create(
			   assemblyName: "DynamicAssembly",
			   syntaxTrees: syntaxTrees,
			   references: _references,
			   options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
		   );

			Assembly assembly;
			using (var ms = new MemoryStream())
			{
				var result = compilation.Emit(ms);

				if (!result.Success)
				{
					string errors = string.Join("\n", result.Diagnostics
											.Where(d => d.Severity == DiagnosticSeverity.Error)
											.Select(d =>
											{
												var span = d.Location.GetMappedLineSpan();
												return $"File: {span.Path}, Line: {span.StartLinePosition.Line + 1} - {d.GetMessage()}";
											}));
					FileM.WriteStringUTF8Async(AppDomain.CurrentDomain.BaseDirectory + @"\" + errorLogFileName, errors).ConfigureAwait(false);
					throw new Exception($"Compilation failed:\n{errors}");
				}

				ms.Seek(0, SeekOrigin.Begin);
				assembly = Assembly.Load(ms.ToArray());
			}

			return assembly;
		}

		// 소스 코드 파일 경로를 받아 컴파일후 인스턴스 생성
		static public List<T> CompileFilesToInstances<T>(string[] sourceFilePaths)
		{
			var assembly = CompileFiles<T>(sourceFilePaths);
			return MakeToInstances<T>(assembly);
		}

		/// <summary>
		/// 어셈블리로 인스턴스 리스크 만들기
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="assembly"></param>
		/// <returns></returns>
		static public List<T> MakeToInstances<T>(Assembly assembly)
		{
			List<T> instances = new List<T>();
			foreach (Type type in assembly.GetTypes())
			{
				if (typeof(T).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
				{
					//_serviceCollection.AddTransient(type);  // 서비스 컬렉션 등록

					T instance = (T)Activator.CreateInstance(type);
					instances.Add(instance);
				}
			}

			//_serviceProvider = _serviceCollection.BuildServiceProvider();   // 서비스 프로바이더 등록
			return instances;
		}

		//public static Type GetService(string typeName)
		//{
		//    var type = Type.GetType(typeName);
		//    return _serviceProvider.GetService(type).GetType();
		//}



		// 파일 내용을 읽어 구문 트리 생성 최적화
		private static SyntaxTree ParseSyntaxTreeFromFile(string filePath)
		{
			using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
			using (StreamReader reader = new StreamReader(fs))
			{
				return CSharpSyntaxTree.ParseText(reader.ReadToEnd(), path: filePath);
			}
		}

		/// <summary>
		/// 파일에서 dll을 로드 한다. 매개변수로 dll만 넘겨야 함
		/// </summary>
		/// <param name="assemblyFilePaths"></param>
		/// <returns></returns>
		public static Assembly[] LoadAssembliesFromFiles(IEnumerable<string> assemblyFilePaths)
		{
			// Assembly 배열을 생성하기 위한 리스트
			List<Assembly> assemblies = new List<Assembly>();

			foreach (var filePath in assemblyFilePaths)
			{
				if (File.Exists(filePath))
				{
					try
					{
						// 어셈블리 로드
						Assembly assembly = Assembly.LoadFrom(filePath);
						assemblies.Add(assembly);
					}
					catch (Exception ex)
					{
						// 로드 중 오류가 발생하면 예외 처리
						Console.WriteLine($"Failed to load assembly from {filePath}: {ex.Message}");
					}
				}
				else
				{
					Console.WriteLine($"Assembly not found at {filePath}");
				}
			}

			// List<Assembly>를 Assembly[]로 변환하여 반환
			return assemblies.ToArray();
		}


		// 단일 소스 코드 파일 경로를 받아 컴파일하여 인스턴스 생성
		public static T CompileFileToInstance<T>(string sourceFilePath)
		{
			// 소스 파일에서 구문 트리 생성
			SyntaxTree syntaxTree = ParseSyntaxTreeFromFile(sourceFilePath);


			// 컴파일 설정
			CSharpCompilation compilation = CSharpCompilation.Create(
				assemblyName: "DynamicAssembly",
				syntaxTrees: new[] { syntaxTree },
				references: _references,
				options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
			);

			// 메모리 스트림에 컴파일 결과 저장
			using (var ms = new MemoryStream())
			{
				var result = compilation.Emit(ms);

				if (!result.Success)
				{
					string errors = string.Join("\n", result.Diagnostics
											.Where(d => d.Severity == DiagnosticSeverity.Error)
											.Select(d =>
											{
												var span = d.Location.GetMappedLineSpan();
												return $"File: {span.Path}, Line: {span.StartLinePosition.Line + 1} - {d.GetMessage()}";
											}));
					FileM.WriteStringUTF8Async(AppDomain.CurrentDomain.BaseDirectory + @"\serverLog.txt", errors);

					throw new Exception($"Compilation failed:\n{errors}");
				}

				ms.Seek(0, SeekOrigin.Begin);
				Assembly assembly = Assembly.Load(ms.ToArray());

				// 인터페이스 T를 구현하는 첫 번째 타입의 인스턴스를 생성하여 반환
				Type type = assembly.GetTypes().FirstOrDefault(t => typeof(T).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
				if (type == null)
				{
					throw new Exception($"No type implementing {typeof(T).Name} found.");
				}

				return (T)Activator.CreateInstance(type);
			}
		}
	}
}