#if TOOLS
#nullable disable

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Framedash.Editor.Logic
{
    internal sealed class MiniJsonReader
    {
        private readonly string _json;
        private int _index;

        private MiniJsonReader(string json)
        {
            _json = json;
        }

        public static bool TryParse(string? json, out object? value)
        {
            value = null;
            if (json == null)
            {
                return false;
            }
            try
            {
                var reader = new MiniJsonReader(json);
                reader.SkipWhitespace();
                if (!reader.TryReadValue(out value))
                {
                    return false;
                }
                reader.SkipWhitespace();
                return reader._index == json.Length;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private bool TryReadValue(out object? value, int depth = 0)
        {
            value = null;
            if (_index >= _json.Length)
            {
                return false;
            }
            switch (_json[_index])
            {
                case '{':
                    return depth < 64 && TryReadObject(out value, depth + 1);
                case '[':
                    return depth < 64 && TryReadArray(out value, depth + 1);
                case '"':
                    if (TryReadString(out string? text))
                    {
                        value = text;
                        return true;
                    }
                    return false;
                case 't':
                    return TryReadLiteral("true", true, out value);
                case 'f':
                    return TryReadLiteral("false", false, out value);
                case 'n':
                    return TryReadLiteral("null", null, out value);
                default:
                    return TryReadNumberValue(out value);
            }
        }

        private bool TryReadObject([NotNullWhen(true)] out object? value, int depth)
        {
            value = null;
            _index++;
            SkipWhitespace();
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (TryConsume('}'))
            {
                value = result;
                return true;
            }
            while (true)
            {
                if (!TryReadString(out string? key))
                {
                    return false;
                }
                SkipWhitespace();
                if (!TryConsume(':'))
                {
                    return false;
                }
                SkipWhitespace();
                if (!TryReadValue(out object? item, depth))
                {
                    return false;
                }
                result[key] = item;
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    value = result;
                    return true;
                }
                if (!TryConsume(','))
                {
                    return false;
                }
                SkipWhitespace();
            }
        }

        private bool TryReadArray([NotNullWhen(true)] out object? value, int depth)
        {
            value = null;
            _index++;
            SkipWhitespace();
            var result = new List<object?>();
            if (TryConsume(']'))
            {
                value = result;
                return true;
            }
            while (true)
            {
                if (!TryReadValue(out object? item, depth))
                {
                    return false;
                }
                result.Add(item);
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    value = result;
                    return true;
                }
                if (!TryConsume(','))
                {
                    return false;
                }
                SkipWhitespace();
            }
        }

        private bool TryReadString([NotNullWhen(true)] out string? value)
        {
            value = null;
            if (!TryConsume('"'))
            {
                return false;
            }
            var builder = new StringBuilder();
            while (_index < _json.Length)
            {
                char character = _json[_index++];
                if (character == '"')
                {
                    value = builder.ToString();
                    return true;
                }
                if (character < ' ')
                {
                    return false;
                }
                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }
                if (_index >= _json.Length)
                {
                    return false;
                }
                char escaped = _json[_index++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (!TryReadUnicodeEscape(out char unicode)) return false;
                        builder.Append(unicode);
                        break;
                    default:
                        return false;
                }
            }
            return false;
        }

        private bool TryReadUnicodeEscape(out char value)
        {
            value = (char)0;
            if (_index + 4 > _json.Length)
            {
                return false;
            }
            int number = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = HexValue(_json[_index++]);
                if (digit < 0)
                {
                    return false;
                }
                number = (number << 4) | digit;
            }
            value = (char)number;
            return true;
        }

        private bool TryReadNumberValue([NotNullWhen(true)] out object? value)
        {
            value = null;
            int start = _index;
            if (TryConsume('-') && _index >= _json.Length)
            {
                return false;
            }
            if (TryConsume('0'))
            {
                if (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    return false;
                }
            }
            else
            {
                if (_index >= _json.Length || _json[_index] < '1' || _json[_index] > '9')
                {
                    return false;
                }
                while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
            }
            if (TryConsume('.'))
            {
                int fractionStart = _index;
                while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                if (_index == fractionStart) return false;
            }
            if (_index < _json.Length && (_json[_index] == 'e' || _json[_index] == 'E'))
            {
                _index++;
                if (_index < _json.Length && (_json[_index] == '+' || _json[_index] == '-'))
                {
                    _index++;
                }
                int exponentStart = _index;
                while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                if (_index == exponentStart) return false;
            }
            string token = _json.Substring(start, _index - start);
            if (!double.TryParse(
                token,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double number) || !IsFinite(number))
            {
                return false;
            }
            value = number;
            return true;
        }

        private bool TryReadLiteral(string literal, object? literalValue, out object? value)
        {
            value = null;
            if (_index + literal.Length > _json.Length
                || string.CompareOrdinal(_json, _index, literal, 0, literal.Length) != 0)
            {
                return false;
            }
            _index += literal.Length;
            value = literalValue;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _json.Length)
            {
                char character = _json[_index];
                if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
                {
                    return;
                }
                _index++;
            }
        }

        private bool TryConsume(char expected)
        {
            if (_index >= _json.Length || _json[_index] != expected)
            {
                return false;
            }
            _index++;
            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static int HexValue(char character)
        {
            if (character >= '0' && character <= '9') return character - '0';
            if (character >= 'a' && character <= 'f') return character - 'a' + 10;
            if (character >= 'A' && character <= 'F') return character - 'A' + 10;
            return -1;
        }
    }
}
#endif
