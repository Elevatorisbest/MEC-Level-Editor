using Frosty.Core.Attributes;
using FrostySdk.Interfaces;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace FrostyCore
{
    public class FrostyLogger : ILogger, INotifyPropertyChanged
    {
        /// <summary>
        /// Text shown in the editor's log box: the newest part of the log. Showing the whole log
        /// made every log line re-layout a text box holding everything logged since startup, which
        /// froze the editor for seconds per line on big levels (copy, delete and launch log
        /// several lines each).
        /// </summary>
        public string LogText
        {
            get
            {
                lock (sync)
                {
                    if (dirty)
                    {
                        cachedText = sb.Length > MaxShownChars
                            ? "(older lines are not shown here)" + Environment.NewLine + sb.ToString(sb.Length - MaxShownChars, MaxShownChars)
                            : sb.ToString();
                        dirty = false;
                    }
                    return cachedText;
                }
            }
        }

        private const int MaxShownChars = 64 * 1024;
        private const int MaxKeptChars = 8 * 1024 * 1024;

        private readonly StringBuilder sb = new StringBuilder();
        private readonly object sync = new object();
        private readonly Dictionary<Assembly, string> categories = new Dictionary<Assembly, string>();
        private string cachedText = "";
        private bool dirty;
        private bool updateScheduled;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Log(string text, params object[] vars)
        {
            Append(Category(Assembly.GetCallingAssembly()), "", text, vars);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void LogWarning(string text, params object[] vars)
        {
            Append(Category(Assembly.GetCallingAssembly()), "(WARNING) ", text, vars);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void LogError(string text, params object[] vars)
        {
            Append(Category(Assembly.GetCallingAssembly()), "(ERROR) ", text, vars);
        }

        private string Category(Assembly assembly)
        {
            lock (sync)
            {
                if (!categories.TryGetValue(assembly, out string category))
                {
                    category = "[Core] ";
                    try
                    {
                        var attr = assembly.GetCustomAttribute<PluginDisplayNameAttribute>();
                        if (attr != null)
                            category = "[" + attr.DisplayName + "] ";
                    }
                    catch (Exception)
                    {
                    }
                    categories[assembly] = category;
                }
                return category;
            }
        }

        private void Append(string category, string prefix, string text, object[] vars)
        {
            string message = text ?? "";
            if (vars != null && vars.Length > 0)
            {
                try
                {
                    message = string.Format(message, vars);
                }
                catch (FormatException)
                {
                    // keep the unformatted text rather than losing the line
                }
            }
            string line = "[" + DateTime.Now.ToLongTimeString() + "]: " + category + prefix + message;

            lock (sync)
            {
                sb.AppendLine(line);
                if (sb.Length > MaxKeptChars)
                    sb.Remove(0, sb.Length - MaxKeptChars / 2);
                dirty = true;
                if (updateScheduled)
                    return;
                updateScheduled = true;
            }

            // one text box update for a burst of lines, after the current work is done
            Application app = Application.Current;
            if (app == null)
            {
                lock (sync)
                    updateScheduled = false;
                RaisePropertyChanged("LogText");
                return;
            }
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (sync)
                    updateScheduled = false;
                RaisePropertyChanged("LogText");
            }), DispatcherPriority.Background);
        }

        public void AddBinding(UIElement elementToBind, DependencyProperty propertyToBind)
        {
            Binding b = new Binding("LogText")
            {
                Source = this,
                Mode = BindingMode.OneWay
            };

            BindingOperations.SetBinding(elementToBind, propertyToBind, b);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void RaisePropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
