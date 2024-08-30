using System;
using System.Collections.Generic;
using System.Linq;
using ModShardLauncher.Mods;

namespace ModShardLauncher;

public class LocalizationModifier : ILocalizationElement
{
    /// <summary>
    /// Id of the modifier
    /// </summary>
    public string Id { get; set; }
    /// <summary>
    /// Dictionary that contains a translation of the modifier as displayed in the log for each available languages.
    /// </summary>
    public Dictionary<ModLanguage, string> Name { get; set; } = new();
    public Dictionary<ModLanguage, string>? Description { get; set; } = null;
    /// <summary>
    /// Return an instance of <see cref="LocalizationModifier"/> with <see cref="Loc"/> filled by an input dictionary.
    /// It is expected to have at least an English key. It does not need to follow the convention order of the localization table.
    /// <example>
    /// For example:
    /// <code>
    /// LocalizationModifier("mySpeechId", 
    ///     new Dictionary &lt; ModLanguage, string &gt; () { {Russian, "speechRu"}, {English, "speechEn"}, {Italian, "speechIt"} });
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="id"></param>
    /// <param name="modifier"></param>
    public LocalizationModifier(string id, Dictionary<ModLanguage, string> name, Dictionary<ModLanguage, string>? description)
    {
        Id = id;
        Name = Localization.SetDictionary(name);
        if (description != null) Description = Localization.SetDictionary(description);
    }
    /// <summary>
    /// Return an instance of <see cref="LocalizationModifier"/> with <see cref="Loc"/> filled by an input string delimited by semi-colon.
    /// It is expected to follow the convention order of the localization table.
    /// <example>
    /// For example:
    /// <code>
    /// LocalizationModifier("mySpeechId", 
    ///     "speechRu;speechEn;speechCh");
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="id"></param>
    /// <param name="modifier"></param>
    public LocalizationModifier(string id, string name, string? description)
    {
        Id = id;
        Name = Localization.SetDictionary(name);
        if (description != null) Description = Localization.SetDictionary(description);
    }
    /// <summary>
    /// Create a string delimited by semi-colon that follows the in-game convention order for localization of speechs.
    /// <example>
    /// For example:
    /// <code>
    /// LocalizationModifier("mySpeechId", "speechRu;speechEn;speechCh").CreateLine();
    /// </code>
    /// returns the string "mySpeechId;speechRu;speechEn;speechCh;speechEn;speechEn;speechEn;speechEn;speechEn;speechEn;speechEn;speechEn;speechEn;".
    /// </example>
    /// </summary>
    /// <returns></returns>
    public IEnumerable<string> CreateLine(string? selector)
    {
        switch(selector)
        {
        case "name":
        yield return $"{Id};{string.Concat(Name.Values.Select(x => @$"{x};"))}";
            break;
        case "description":
        if (Description == null) yield return $"{Id};{string.Concat(Enumerable.Repeat("None;", Msl.ModLanguageSize))}";
        else yield return $"{Id};{string.Concat(Description.Values.Select(x => @$"{x};"))}";
            break;
        }
    }
}
public static partial class Msl
{
    /// <summary>
    /// Wrapper for the LocalizationModifiers class
    /// </summary>
    /// <param name="modifiers"></param>
    public static void InjectTableModifiersLocalization(params LocalizationModifier[] modifiers)
    {
        LocalizationBaseTable localizationBaseTable = new("gml_GlobalScript_table_Modifiers",
            ("buff_name;", "name"), ("buff_desc;", "description")
        );
        localizationBaseTable.InjectTable(modifiers.Select(x => x as ILocalizationElement).ToList());
    }
}