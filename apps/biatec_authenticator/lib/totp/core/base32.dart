import 'dart:typed_data';

/// A string that is not valid Base32. The message never contains the input, which may be a secret.
class Base32Exception implements Exception {
  const Base32Exception(this.message,
      {this.empty = false, this.tooShort = false});
  final String message;

  /// True when there was no key at all, only spaces, dashes or padding.
  final bool empty;

  /// True when the key is valid Base32 but too short to make one byte: a weak key rather than a broken one.
  final bool tooShort;

  @override
  String toString() => 'Base32Exception: $message';
}

/// Base32 (RFC 4648 section 6), the encoding of one-time password secrets.
abstract final class Base32 {
  static const _alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

  static final Map<int, int> _values = {
    for (var i = 0; i < _alphabet.length; i++) _alphabet.codeUnitAt(i): i,
  };

  /// The value of a Base32 character in either case, or null.
  static int? _value(int u) => _values[u >= 0x61 && u <= 0x7A ? u - 0x20 : u];

  /// `=` characters that complete a last group of 2, 4, 5 or 7 characters to 8.
  static const _padFor = {0: 0, 2: 6, 4: 4, 5: 3, 7: 1};

  /// Whitespace (including the no-break and thin spaces web pages lay keys out with) and dashes.
  static bool _separator(int u) =>
      u == 0x20 ||
      u == 0x09 ||
      u == 0x0A ||
      u == 0x0D ||
      u == 0x2D ||
      u == 0x00A0 ||
      u == 0x1680 ||
      (u >= 0x2000 && u <= 0x200A) ||
      u == 0x2010 || // hyphen
      u == 0x2011 || // non-breaking hyphen
      u == 0x202F ||
      u == 0x205F ||
      u == 0x3000;

  /// Decodes [input]. Case, spaces, dashes and missing `=` padding are tolerated, because people copy setup keys from web pages in groups of four. Padding, when present, must be the exact amount; anything else outside the alphabet is refused.
  ///
  /// The key goes straight into the returned bytes, with no other copy left behind: callers wipe that one buffer.
  static Uint8List decode(String input) {
    // First pass: check and count the key characters and the padding after them, without copying them. A character outside the alphabet is reported before any length or padding problem, since it is the usual mistake (0, 1 or 8 typed for O, I or B).
    var count = 0;
    var pads = 0;
    var padEnded = false;
    for (final u in input.codeUnits) {
      // Whitespace and dashes group the key for reading; they carry nothing. Padding is one run, so a separator ends it.
      if (_separator(u)) {
        if (pads > 0) padEnded = true;
        continue;
      }
      if (u == 0x3D) {
        if (padEnded) {
          throw const Base32Exception('The key has broken-up padding.');
        }
        pads++;
      } else if (pads > 0) {
        // Padding is only allowed at the end.
        throw const Base32Exception('The key has padding in the middle.');
      } else if (_value(u) == null) {
        throw const Base32Exception('The key has a character that is not '
            'part of Base32 (letters A to Z and digits 2 to 7).');
      } else {
        count++;
      }
    }
    if (count == 0) {
      throw const Base32Exception('The key is empty.', empty: true);
    }
    // Leftover bits that do not fill a byte are dropped, as other authenticator apps do, whatever the length: some services hand out such keys, and refusing would lock the user out of that account. Padding is defined only for whole-byte lengths, so it must then be exact.
    final want = _padFor[count % 8];
    if (pads != 0 && pads != want) {
      throw const Base32Exception('The key has the wrong padding.');
    }
    final out = Uint8List(count * 5 ~/ 8);
    if (out.isEmpty) {
      throw const Base32Exception('The key is too short.', tooShort: true);
    }
    var n = 0;
    var buffer = 0;
    var bits = 0;
    for (final u in input.codeUnits) {
      if (_separator(u)) continue;
      if (u == 0x3D) break;
      // Checked in the first pass.
      final v = _value(u)!;
      buffer = (buffer << 5) | v;
      bits += 5;
      if (bits >= 8) {
        bits -= 8;
        out[n++] = (buffer >> bits) & 0xFF;
        buffer &= (1 << bits) - 1;
      }
    }
    return out;
  }

  /// Encodes [bytes] in upper case without padding, as services show setup keys.
  static String encode(List<int> bytes) {
    final sb = StringBuffer();
    var buffer = 0;
    var bits = 0;
    for (final b in bytes) {
      buffer = (buffer << 8) | (b & 0xFF);
      bits += 8;
      while (bits >= 5) {
        bits -= 5;
        sb.write(_alphabet[(buffer >> bits) & 0x1F]);
      }
      buffer &= (1 << bits) - 1;
    }
    if (bits > 0) sb.write(_alphabet[(buffer << (5 - bits)) & 0x1F]);
    return sb.toString();
  }
}
