using System.Threading.Tasks;

namespace EcsServerLibM
{

	public interface IExecutableM
	{
		public void Execute();
	}

	public interface ICancelM
	{
		public void Cancel();
	}

	public interface IExecutableValueAsyncM
	{
		public ValueTask Execute();
	}

	public interface IExecutableAsyncM
	{
		public Task Execute();
	}
}
