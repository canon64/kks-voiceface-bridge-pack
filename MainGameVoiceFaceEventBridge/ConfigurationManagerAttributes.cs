using System;
using BepInEx.Configuration;

namespace ConfigurationManager
{
#pragma warning disable CS0649
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? ShowRangeAsPercent;
        public bool? Browsable;
        public string Category;
        public object DefaultValue;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public string Description;
        public bool? IsAdvanced;
        public int? Order;
        public bool? ReadOnly;
        public Action<ConfigEntryBase> CustomDrawer;
        public Func<object, string> ObjToStr;
        public Func<string, object> StrToObj;
    }
#pragma warning restore CS0649
}
