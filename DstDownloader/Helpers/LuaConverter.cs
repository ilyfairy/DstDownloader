using MoonSharp.Interpreter;

namespace DstDownloaders.Helpers;

public static class LuaConverter
{
    public static object? ToClrObject(object luaValue)
    {
        if(luaValue is DynValue dyn)
        {
            luaValue = dyn.ToObject();
        }

        if(luaValue is Table table)
        {
            bool isArray = true;
            int minValue = 1;
            int maxValue = table.Length;
            foreach (var item in table.Keys)
            {
                if(item.IsNumber)
                {
                    minValue = int.Min((int)item.Number, minValue);
                    maxValue = int.Max((int)item.Number, maxValue);
                    break;
                }
                else
                {
                    isArray = false;
                    break;
                }
            }
            if(minValue != 1 || maxValue != table.Length)
            {
                isArray = false;
            }

            if(isArray)
            {
                var list = new List<object?>();
                for (int i = minValue; i <= maxValue; i++)
                {
                    list.Add(ToClrObject(table.Get(i)));
                }
                return list.ToArray();
            }
            else
            {
                var dict = new Dictionary<string, object?>();
                foreach (var item in table.Pairs)
                {
                    if (!item.Key.IsString)
                        continue;

                    dict.Add(item.Key.String, ToClrObject(item.Value));
                }
                return dict;
            }
        }

        if (luaValue is int or double or string or float or bool)
            return luaValue;

         return null;
    }

}
