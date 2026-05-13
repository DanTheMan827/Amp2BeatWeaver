using System;
using System.Collections.Generic;

namespace DtxCS.DataTypes
{
  public class DataAtom : DataNode
  {
    DataType type;
    string sData;
    int iData;
    float fData;

    public override DataType Type => type;
    public override DataNode Evaluate() => this;

    public int Int
    {
      get
      {
        if (type == DataType.INT) return iData;
        throw new Exception("Data is not int");
      }
    }

    public float Float
    {
      get
      {
        if (type == DataType.FLOAT) return fData;
        throw new Exception("Data is not float");
      }
    }

    public string String
    {
      get
      {
        if (type == DataType.STRING) return sData;
        throw new Exception("Data is not string");
      }
    }

    public DataAtom(string data) { type = DataType.STRING; sData = data.Replace("\\q", "\""); }
    public DataAtom(int data)    { type = DataType.INT;    iData = data; }
    public DataAtom(float data)  { type = DataType.FLOAT;  fData = data; }

    public override string Name => ToString(true);

    private string ToString(bool name)
    {
      switch (type)
      {
        case DataType.STRING: return name ? sData : "\"" + sData.Replace("\"", "\\\"") + "\"";
        case DataType.INT:    return iData.ToString();
        case DataType.FLOAT:  return fData.ToString("0.0#", System.Globalization.NumberFormatInfo.InvariantInfo);
        default:              return "";
      }
    }

    public override string ToString() => ToString(false);
  }

  public class DataVariable : DataNode
  {
    static Dictionary<string, DataVariable> vars = new Dictionary<string, DataVariable>();
    public override string Name { get; }
    public override string ToString() => Name;
    public override DataType Type => DataType.VARIABLE;
    public DataNode Value { get; set; }
    public override DataNode Evaluate() => Value;

    public static DataVariable Var(string name)
    {
      if (!vars.TryGetValue(name, out DataVariable ret))
        vars.Add(name, ret = new DataVariable(name, new DataAtom(0)));
      return ret;
    }

    private DataVariable(string name, DataNode value)
    {
      Name = "$" + name;
      Value = value;
    }
  }

  public class DataSymbol : DataNode
  {
    static Dictionary<string, DataSymbol> symbols = new Dictionary<string, DataSymbol>();

    public static DataSymbol Symbol(string value)
    {
      if (!symbols.TryGetValue(value, out DataSymbol ret))
        symbols.Add(value, ret = new DataSymbol(value));
      return ret;
    }

    public override string Name => value;
    public override DataNode Evaluate() => this;
    public override DataType Type => DataType.SYMBOL;

    private string value;
    private bool quote;

    private DataSymbol(string value)
    {
      this.value = value;
      foreach (var c in value)
      {
        if (c == ' ' || c == '\r' || c == '\n' || c == '\t'
          || c == '(' || c == ')' || c == '{' || c == '}'
          || c == '[' || c == ']')
        { quote = true; break; }
      }
    }

    public override string ToString() => quote ? $"'{Name}'" : Name;
  }
}
