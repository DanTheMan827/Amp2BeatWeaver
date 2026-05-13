using System;
using System.Collections.Generic;

namespace DtxCS.DataTypes
{
  public class DataArray : DataNode
  {
    public virtual char ClosingChar => ')';
    public override DataType Type => DataType.ARRAY;
    public List<DataNode> Children { get; }

    public DataArray() { this.Children = new List<DataNode>(); }

    public T AddNode<T>(T node) where T : DataNode
    {
      Children.Add(node);
      node.Parent = this;
      return node;
    }

    public DataNode this[int index]
    {
      get { return Children[index]; }
      set { Children[index] = value; }
    }

    public DataArray Array(int idx)
    {
      if (Children[idx].Type == DataType.ARRAY)
        return (DataArray)Children[idx];
      throw new Exception("Element at index " + idx + " is not an Array. It is " + Children[idx].GetType().Name);
    }

    public int Int(int idx)
    {
      if (Children[idx].Type == DataType.INT)
        return ((DataAtom)Children[idx]).Int;
      throw new Exception("Element at index " + idx + " is not an integer.");
    }

    public float Float(int idx)
    {
      if (Children[idx].Type == DataType.FLOAT)
        return ((DataAtom)Children[idx]).Float;
      throw new Exception("Element at index " + idx + " is not a float.");
    }

    public float Number(int idx)
    {
      if (Children[idx].Type == DataType.FLOAT) return ((DataAtom)Children[idx]).Float;
      if (Children[idx].Type == DataType.INT)   return ((DataAtom)Children[idx]).Int;
      throw new Exception("Element at index " + idx + " is not a number.");
    }

    public DataNode Node(int idx) => Children[idx];

    public string String(int idx)
    {
      if (Children[idx].Type == DataType.STRING)
        return ((DataAtom)Children[idx]).String;
      throw new Exception("Element at index " + idx + " is not a string.");
    }

    public DataSymbol Symbol(int idx)
    {
      if (Children[idx].Type == DataType.SYMBOL)
        return (DataSymbol)Children[idx];
      throw new Exception("Element at index " + idx + " is not a symbol.");
    }

    public DataVariable Var(int idx)
    {
      if (Children[idx].Type == DataType.VARIABLE)
        return (DataVariable)Children[idx];
      throw new Exception("Element at index " + idx + " is not a variable.");
    }

    public string Any(int idx) => Children[idx].Name;

    public DataArray Array(string name)
    {
      for (int i = 0; i < Children.Count; i++)
      {
        if (!(Children[i] is DataArray)) continue;
        if (Children[i].Name == name)
          return (DataArray)Children[i];
      }
      return null;
    }

    public override DataNode Evaluate()
    {
      var returnArray = new DataArray();
      foreach (var node in Children)
        returnArray.AddNode(node.Evaluate());
      return returnArray;
    }

    public override string Name => Children.Count > 0 && Children[0].Type != DataType.ARRAY
                                   ? Children[0].Name : "";

    public int Count => Children.Count;

    public override string ToString() => ToString(0);

    public override string ToString(int depth)
    {
      string ret = new string(' ', depth * 3) + "(";
      for (int i = 0; i < Children.Count; i++)
      {
        var n = Children[i];
        ret += n is DataArray ? Environment.NewLine + n.ToString(depth + 1) : n.ToString(depth + 1);
        if (i + 1 != Children.Count) ret += " ";
      }
      ret += ")";
      return ret;
    }
  }

  public class DataCommand : DataArray
  {
    public override DataType Type => DataType.COMMAND;
    public override char ClosingChar => '}';
    public DataCommand() : base() { }

    public DataCommand EvalAll()
    {
      var ret = new DataCommand();
      for (var i = 0; i < Children.Count; i++)
        ret.AddNode(Children[i].Evaluate());
      return ret;
    }

    public override DataNode Evaluate()
    {
      Func<DataCommand, DataNode> f;
      if (Builtins.Funcs.TryGetValue(Symbol(0), out f))
        return f.Invoke(this);
      throw new Exception($"Func '{Any(0)}' is not defined.");
    }

    public override string ToString() => ToString(0);

    public override string ToString(int depth)
    {
      string ret = new string(' ', depth * 3) + "{";
      foreach (DataNode n in Children)
        ret += (n is DataArray ? Environment.NewLine + n.ToString(depth + 1) : n.ToString(depth + 1) + " ");
      ret += "}";
      return ret;
    }
  }

  public class DataMacroDefinition : DataArray
  {
    public override DataType Type => DataType.MACRO;
    public override char ClosingChar => ']';
    public DataMacroDefinition() : base() { }
    public override DataNode Evaluate() => this;

    public override string ToString() => ToString(0);

    public override string ToString(int depth)
    {
      string ret = new string(' ', depth * 3) + "[";
      foreach (DataNode n in Children)
        ret += (n is DataArray ? Environment.NewLine + n.ToString(depth + 1) : n.ToString(depth + 1) + " ");
      ret += "]";
      return ret;
    }
  }
}
