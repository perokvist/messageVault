using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MessageVault.Hosting
{
	class DisposableAction : IDisposable
	{
		readonly Action _action;

		public DisposableAction(Action action)
		{
			_action = action;
		}

		public void Dispose()
		{
			_action();
		}

		public static IDisposable Empty() {
			return new DisposableAction(() => {});
		}
	}
}
