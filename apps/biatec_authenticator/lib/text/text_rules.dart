/// The text rules of protocol 1.2 for every string a relying party supplies (specification chapter 15, the relying-party profile; `isRelyingPartyText` in
/// `packages/consent-protocol/scripts/lib.mjs`). A violation is refused, never stripped, normalised or truncated: the phone does not trust the server that sent the string.
///
/// 1. Unicode NFC. Dart has no Unicode normaliser, so NFC cannot be computed here; instead a combining mark after a Latin, Greek or Cyrillic letter (which all have precomposed forms) is refused,
///    which is what text that is not in NFC looks like in practice. The server and every verifier still check NFC for everything else.
/// 2. Length counted in code points, at least 1 and at most `max`.
/// 3. No control character, 4. no bidirectional control, 5. no zero-width character (ZWNJ and ZWJ included: this profile has no exception), 6. no line break.
/// 7. No other format or private-use character, no white space other than a plain space, no padding, no run of spaces, no blank text, no filler or invisible character,
///    no combining mark without a base character or in a stack of more than three, no lone surrogate.
///
/// Look-alike letters of another script are not detected: the text is always drawn as written by the service, beside the proven domain or the fingerprint and never instead of them.
library;

/// Format, control, private-use, line and paragraph separators; white space other than a plain space; a run of spaces.
final RegExp _notAllowedSpaceOrFormat =
    RegExp(r'[\p{Cf}\p{Cc}\p{Co}\p{Zl}\p{Zp}]|[^\S ]|  ', unicode: true);

/// Invisible or filler characters outside the general categories above (soft hyphen, fillers, invisible operators, variation selectors, tags).
final RegExp _invisible = RegExp(
    r'[­͏ᅟᅠ឴឵᠋-᠎⁡-⁥⁪-⁯⠀ㅤ︀-️ﾠ￹-￼]|[\u{1D173}-\u{1D17A}\u{E0000}-\u{E007F}\u{E0100}-\u{E01EF}]',
    unicode: true);

final RegExp _bareOrStackedMark =
    RegExp(r'(?:^|\s)\p{M}|\p{M}{4}', unicode: true);

final RegExp _markAfterComposable = RegExp(
    r'[\p{Script=Latin}\p{Script=Greek}\p{Script=Cyrillic}]\p{M}',
    unicode: true);

bool _wellFormed(String text) {
  final units = text.codeUnits;
  for (var i = 0; i < units.length; i++) {
    final u = units[i];
    if (u >= 0xD800 && u <= 0xDBFF) {
      if (i + 1 >= units.length || units[i + 1] < 0xDC00 || units[i + 1] > 0xDFFF) {
        return false;
      }
      i++;
    } else if (u >= 0xDC00 && u <= 0xDFFF) {
      return false;
    }
  }
  return true;
}

/// Whether [text] is acceptable text from a relying party: at most [max] code points and none of the refused characters.
bool isRelyingPartyText(Object? text, int max) {
  if (text is! String || text.isEmpty || !_wellFormed(text)) return false;
  if (text.runes.length > max) return false;
  // No padding, not blank (the trim of Dart and of JavaScript cover the same white space).
  if (text.trim() != text || text.trim().isEmpty) return false;
  if (_bareOrStackedMark.hasMatch(text)) return false;
  if (_markAfterComposable.hasMatch(text)) return false;
  return !_notAllowedSpaceOrFormat.hasMatch(text) && !_invisible.hasMatch(text);
}
