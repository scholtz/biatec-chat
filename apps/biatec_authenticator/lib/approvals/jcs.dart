import 'dart:convert';

/// RFC 8785 JSON Canonicalization Scheme, the exact bytes the device signs.
///
/// Accepts maps with string keys, lists, strings, booleans, null and numbers.
/// Members are sorted by UTF-16 code units (Dart's [String.compareTo]),
/// strings use the minimal JSON escapes and numbers the ECMAScript form.
/// Throws [ArgumentError] on anything else, a lone surrogate or a non-finite
/// number.
String canonicalize(Object? value) {
  final out = StringBuffer();
  _write(out, value);
  return out.toString();
}

/// UTF-8 bytes of [canonicalize].
List<int> canonicalBytes(Object? value) => utf8.encode(canonicalize(value));

void _write(StringBuffer out, Object? value) {
  if (value == null || value is bool) {
    out.write(value);
  } else if (value is String) {
    _string(out, value);
  } else if (value is num) {
    out.write(_number(value));
  } else if (value is List) {
    out.write('[');
    for (var i = 0; i < value.length; i++) {
      if (i > 0) out.write(',');
      _write(out, value[i]);
    }
    out.write(']');
  } else if (value is Map) {
    final keys = value.keys.map((k) {
      if (k is! String) throw ArgumentError('map keys must be strings');
      return k;
    }).toList()
      ..sort();
    out.write('{');
    for (var i = 0; i < keys.length; i++) {
      if (i > 0) out.write(',');
      _string(out, keys[i]);
      out.write(':');
      _write(out, value[keys[i]]);
    }
    out.write('}');
  } else {
    throw ArgumentError('cannot canonicalize ${value.runtimeType}');
  }
}

void _string(StringBuffer out, String s) {
  for (var i = 0; i < s.length; i++) {
    final c = s.codeUnitAt(i);
    if (c >= 0xD800 && c <= 0xDBFF) {
      final next = i + 1 < s.length ? s.codeUnitAt(i + 1) : 0;
      if (next < 0xDC00 || next > 0xDFFF) {
        throw ArgumentError('lone surrogate');
      }
      i++;
    } else if (c >= 0xDC00 && c <= 0xDFFF) {
      throw ArgumentError('lone surrogate');
    }
  }
  // Dart's encoder emits the same minimal escapes as ECMAScript
  // JSON.stringify for well-formed strings.
  out.write(jsonEncode(s));
}

String _number(num n) {
  if (n is int) {
    // Beyond 2^53 - 1 an ECMAScript verifier would round the value.
    if (n.abs() > 9007199254740991) throw ArgumentError('integer too large');
    return n.toString();
  }
  final d = n.toDouble();
  if (!d.isFinite) throw ArgumentError('non-finite number');
  if (d == 0) return '0'; // also -0
  // Integral values below 1e21 print without exponent or fraction in
  // ECMAScript; Dart would append ".0".
  if (d == d.truncateToDouble() && d.abs() < 1e21) {
    return BigInt.from(d).toString();
  }
  // Otherwise Dart and ECMAScript agree on the shortest round-trip digits and
  // the exponent form ("1e+30", "1e-7").
  return d.toString();
}
