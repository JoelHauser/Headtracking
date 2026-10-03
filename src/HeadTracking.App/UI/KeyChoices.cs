using System.Collections.Generic;

namespace HeadTracking.App.UI
{
    /// <summary>A key the game can watch for, by its Unity KeyCode value.</summary>
    public sealed class KeyChoice
    {
        public string Name { get; set; }
        public int Code { get; set; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// Keys offered for the in-game hotkeys, with Unity's KeyCode values (read from the game's
    /// UnityEngine.CoreModule: F1 282, Keypad0 256, Insert 277, Mouse3 326, ...). The plugin
    /// checks the value is a defined KeyCode before using it.
    /// </summary>
    public static class KeyChoices
    {
        public static readonly List<KeyChoice> All = Build();

        private static List<KeyChoice> Build()
        {
            List<KeyChoice> keys = new List<KeyChoice> { new KeyChoice { Name = "(none)", Code = 0 } };
            for (int i = 1; i <= 15; i++)
            {
                keys.Add(new KeyChoice { Name = "F" + i, Code = 281 + i });
            }

            keys.Add(new KeyChoice { Name = "Insert", Code = 277 });
            keys.Add(new KeyChoice { Name = "Home", Code = 278 });
            keys.Add(new KeyChoice { Name = "End", Code = 279 });
            keys.Add(new KeyChoice { Name = "Page Up", Code = 280 });
            keys.Add(new KeyChoice { Name = "Page Down", Code = 281 });
            keys.Add(new KeyChoice { Name = "Delete", Code = 127 });
            keys.Add(new KeyChoice { Name = "Pause", Code = 19 });
            keys.Add(new KeyChoice { Name = "Scroll Lock", Code = 302 });
            for (int i = 0; i <= 9; i++)
            {
                keys.Add(new KeyChoice { Name = "Numpad " + i, Code = 256 + i });
            }

            keys.Add(new KeyChoice { Name = "Numpad .", Code = 266 });
            keys.Add(new KeyChoice { Name = "Numpad /", Code = 267 });
            keys.Add(new KeyChoice { Name = "Numpad *", Code = 268 });
            keys.Add(new KeyChoice { Name = "Numpad -", Code = 269 });
            keys.Add(new KeyChoice { Name = "Numpad +", Code = 270 });
            keys.Add(new KeyChoice { Name = "Numpad Enter", Code = 271 });
            for (int i = 0; i < 26; i++)
            {
                keys.Add(new KeyChoice { Name = ((char)('A' + i)).ToString(), Code = 97 + i });
            }

            for (int i = 0; i <= 9; i++)
            {
                keys.Add(new KeyChoice { Name = i.ToString(), Code = 48 + i });
            }

            keys.Add(new KeyChoice { Name = "Mouse 4 (back)", Code = 326 });
            keys.Add(new KeyChoice { Name = "Mouse 5 (forward)", Code = 327 });
            keys.Add(new KeyChoice { Name = "Mouse 6", Code = 328 });
            keys.Add(new KeyChoice { Name = "Mouse 7", Code = 329 });
            return keys;
        }
    }
}
