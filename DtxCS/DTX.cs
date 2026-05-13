using System;
using System.Globalization;
using System.Text;
using DtxCS.DataTypes;

namespace DtxCS
{
  public static class DTX
  {
    /// <summary>
    /// Parses a plaintext DTA string into a DataArray.
    /// </summary>
    public static DataArray FromDtaString(string data)
    {
      DataArray root = new DataArray();
      ParseString(data, root);
      return root;
    }

    /// <summary>
    /// Parses a plaintext DTA stream into a DataArray.
    /// </summary>
    public static DataArray FromDtaStream(System.IO.Stream data)
      => FromDtaString(new UTF8Encoding(false).GetString(data.ReadBytes((int)data.Length)));

    enum ParseState
    {
      whitespace,
      in_string,
      in_literal,
      in_symbol,
      in_comment,
      in_directive,
      in_constant
    }

    private static int ParseDefine(string data, DataArray root)
    {
      int parsedCharacters = 0;
      int layersDeep = 0;
      int start = 0;
      for (parsedCharacters = 0; parsedCharacters < data.Length; parsedCharacters++)
      {
        switch (data[parsedCharacters])
        {
          case '(':
            if (layersDeep == 0) start = parsedCharacters + 1;
            layersDeep++;
            break;
          case ')':
            layersDeep--;
            if (layersDeep == 0) goto DoneParsing;
            break;
        }
      }
      DoneParsing:
      if (layersDeep != 0)
        throw new Exception("Mismatching brackets in parsing #define directive.");
      ParseString(data.Substring(start, parsedCharacters - start), root);
      return parsedCharacters;
    }

    private static void ParseString(string data, DataArray root)
    {
      ParseState state = ParseState.whitespace;
      data += " ";
      DataArray current = root;
      string tmp_literal = "";
      string tmp_directive = "";
      string tmp_constant = "";
      bool escaping = false;
      int line = 1;

      for (int i = 0; i < data.Length; i++)
      {
        if (data[i] == '\uFEFF') continue;
        if (data[i] == '\n') line++;

        switch (state)
        {
          case ParseState.whitespace:
            switch (data[i])
            {
              case '\'':
                tmp_literal = "";
                state = ParseState.in_symbol;
                break;
              case '"':
                tmp_literal = "";
                state = ParseState.in_string;
                break;
              case ';':
                tmp_literal = "";
                state = ParseState.in_comment;
                break;
              case ' ': case '\r': case '\n': case '\t':
                continue;
              case '}': case ')': case ']':
                if (data[i] != current.ClosingChar || current.Parent == null)
                  throw new Exception($"Mismatched closing brace encountered at line {line}.");
                current = current.Parent;
                break;
              case '(':
                current = (DataArray)current.AddNode(new DataArray());
                break;
              case '{':
                current = (DataArray)current.AddNode(new DataCommand());
                break;
              case '[':
                current = (DataArray)current.AddNode(new DataMacroDefinition());
                break;
              case '#':
                state = ParseState.in_directive;
                tmp_directive = "";
                break;
              default:
                state = ParseState.in_literal;
                tmp_literal = new string(data[i], 1);
                continue;
            }
            break;

          case ParseState.in_directive:
            switch (data[i])
            {
              case ' ': case '\t': case '\r': case '\n':
                switch (tmp_directive)
                {
                  case "else":    current.AddNode(new DataElse());    state = ParseState.whitespace; break;
                  case "endif":   current.AddNode(new DataEndIf());   state = ParseState.whitespace; break;
                  case "autorun": current.AddNode(new DataAutorun()); state = ParseState.whitespace; break;
                  default:        state = ParseState.in_constant; tmp_constant = ""; break;
                }
                break;
              default:
                tmp_directive += data[i];
                continue;
            }
            break;

          case ParseState.in_constant:
            switch (data[i])
            {
              case ' ': case '\t': case '\r': case '\n': case ')': case '}': case ']':
                switch (tmp_directive)
                {
                  case "define":
                    DataArray def = new DataArray();
                    i += ParseDefine(data.Substring(i), def);
                    current.AddNode(new DataDefine(tmp_constant, def));
                    break;
                  case "ifdef":   current.AddNode(new DataIfDef(tmp_constant));  break;
                  case "ifndef":  current.AddNode(new DataIfNDef(tmp_constant)); break;
                  case "include": current.AddNode(new DataInclude(tmp_constant)); break;
                  case "merge":   current.AddNode(new DataMerge(tmp_constant));  break;
                  case "undef":   current.AddNode(new DataUndef(tmp_constant));  break;
                  default:
                    AddLiteral(current, tmp_directive);
                    AddLiteral(current, tmp_constant);
                    break;
                }
                state = ParseState.whitespace;
                break;
              default:
                tmp_constant += data[i];
                continue;
            }
            break;

          case ParseState.in_string:
            switch (data[i])
            {
              case '\\':
                escaping = true;
                break;
              case '"':
                if (escaping) { tmp_literal += data[i]; escaping = false; break; }
                current.AddNode(new DataAtom(tmp_literal));
                state = ParseState.whitespace;
                break;
              default:
                tmp_literal += data[i];
                continue;
            }
            break;

          case ParseState.in_literal:
            switch (data[i])
            {
              case ' ': case '\r': case '\n': case '\t':
                AddLiteral(current, tmp_literal);
                state = ParseState.whitespace;
                break;
              case '}': case ')': case ']':
                AddLiteral(current, tmp_literal);
                if (data[i] != current.ClosingChar)
                  throw new Exception("Mismatched brace types encountered.");
                current = current.Parent;
                state = ParseState.whitespace;
                break;
              case '(':
                AddLiteral(current, tmp_literal);
                current = (DataArray)current.AddNode(new DataArray());
                break;
              case '{':
                AddLiteral(current, tmp_literal);
                current = (DataArray)current.AddNode(new DataCommand());
                break;
              case '[':
                AddLiteral(current, tmp_literal);
                current = (DataArray)current.AddNode(new DataMacroDefinition());
                break;
              default:
                tmp_literal += data[i];
                continue;
            }
            break;

          case ParseState.in_symbol:
            switch (data[i])
            {
              case '\r': case '\n': case '\t':
                throw new Exception("Whitespace encountered in symbol.");
              case '\'':
                current.AddNode(DataSymbol.Symbol(tmp_literal));
                state = ParseState.whitespace;
                break;
              default:
                tmp_literal += data[i];
                continue;
            }
            break;

          case ParseState.in_comment:
            switch (data[i])
            {
              case '\r': case '\n': state = ParseState.whitespace; break;
              default: continue;
            }
            break;
        }
      }
    }

    private static void AddLiteral(DataArray current, string tmp_literal)
    {
      if (string.IsNullOrEmpty(tmp_literal)) return;
      if (int.TryParse(tmp_literal, NumberStyles.Integer, NumberFormatInfo.InvariantInfo, out int tmp_int))
        current.AddNode(new DataAtom(tmp_int));
      else if (float.TryParse(tmp_literal, NumberStyles.Float, NumberFormatInfo.InvariantInfo, out float tmp_float))
        current.AddNode(new DataAtom(tmp_float));
      else if (tmp_literal[0] == '$')
        current.AddNode(DataVariable.Var(tmp_literal.Substring(1)));
      else
        current.AddNode(DataSymbol.Symbol(tmp_literal));
    }
  }
}
