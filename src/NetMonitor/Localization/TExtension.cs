using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace NetMonitor.Localization
{
    /// <summary>
    /// Texto traduzido no XAML: <c>Text="{l:T Settings_Title}"</c>.
    /// É uma binding, então a interface acompanha a troca de idioma em tempo de execução.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension()
        {
        }

        public TExtension(string key) => Key = key;

        [ConstructorArgument("key")]
        public string Key { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
