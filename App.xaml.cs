using System.Configuration;
using System.Data;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace StarCitizenOverLay
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _serviceProvider;

        public App()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddWpfBlazorWebView();
            services.AddSingleton<OverlaySearchApiService>();

            _serviceProvider = services.BuildServiceProvider();
            Resources.Add("services", _serviceProvider);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider.Dispose();
            base.OnExit(e);
        }
    }

}
