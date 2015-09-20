using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MessageVault.Server;
using Microsoft.Owin;
using Microsoft.Owin.Hosting;
using Nancy.Owin;
using Owin;

namespace MessageVault.Hosting
{
	public static class Extensions
	{
		//TODO in "standard" way
		public static IAppBuilder UseMessageVault(this IAppBuilder appBuilder, AppConfig config)
		{

			//TODO logging, timeout time
			var app = App.Initialize(config, (options, nancyoptions) => {
				appBuilder.UseNancy(nancyoptions);
				return DisposableAction.Empty(); //TODO log
			});

			var token = new OwinContext(appBuilder.Properties)
				.Get<CancellationToken>(Constants.OwinDispose);
			if (token != CancellationToken.None)
			{
				token.Register(() =>
				{
					app.RequestStop();
					app.GetCompletionTask().Wait();
				});
			}
			return appBuilder;
		}
	}
}
