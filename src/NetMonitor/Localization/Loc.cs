using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading;

namespace NetMonitor.Localization
{
    public sealed record LanguageOption(string Code, string Name);

    /// <summary>
    /// Ponto único de acesso aos textos da interface (Localization/Strings*.resx).
    ///
    /// Para adicionar um idioma: copie <c>Strings.resx</c> para <c>Strings.&lt;código&gt;.resx</c>
    /// (ex.: <c>Strings.en.resx</c>), traduza os valores e inclua o idioma em <see cref="Supported"/>.
    /// Em XAML use <c>{l:T Chave}</c>; em C# use <c>Loc.T("Chave")</c> ou <c>Loc.T("Chave", args)</c>.
    /// </summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        public const string DefaultLanguage = "pt-BR";

        private static readonly ResourceManager Resources =
            new("NetMonitor.Localization.Strings", typeof(Loc).Assembly);

        public static Loc Instance { get; } = new();

        /// <summary>Idiomas disponíveis na aba Configurações.</summary>
        public static IReadOnlyList<LanguageOption> Supported { get; } = new[]
        {
            new LanguageOption("pt-BR", "Português (Brasil)"),
            // new LanguageOption("en", "English"),
        };

        private Loc()
        {
        }

        public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLanguage);

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Usado pelas bindings do XAML: <c>[Chave]</c>.</summary>
        public string this[string key] => Resources.GetString(key, Culture) ?? $"[{key}]";

        public static string T(string key) => Instance[key];

        public static string T(string key, params object?[] args) =>
            string.Format(Instance.Culture, Instance[key], args);

        public void SetLanguage(string? code)
        {
            var option = Supported.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
                         ?? Supported[0];
            Culture = CultureInfo.GetCultureInfo(option.Code);
            Thread.CurrentThread.CurrentUICulture = Culture;
            CultureInfo.DefaultThreadCurrentUICulture = Culture;
            // "Item[]" avisa o WPF que todos os valores do indexador mudaram.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        }
    }
}
