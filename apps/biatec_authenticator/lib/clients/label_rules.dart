/// The text rules for the user's own name of a service, the same as Biatec OIDC applies (specification section 8.2, `isDisplaySafe` in `packages/consent-protocol/scripts/lib.mjs`):
/// at most 40 code points, no control, bidirectional, zero-width, line-break or invisible characters, no padding, not blank, no stacked or bare combining marks. A violation is
/// refused, never stripped.
///
/// Dart has no Unicode normaliser, so NFC cannot be checked here. Instead a combining mark after a Latin, Greek or Cyrillic letter is refused (those letters have precomposed forms,
/// so such a text may not be NFC); the server still checks NFC for everything else.
library;

const int labelMaxLength = 40;

/// Why a label is refused.
enum LabelProblem { empty, tooLong, padded, notAllowed }

final RegExp _notAllowedSpaceOrFormat =
    RegExp(r'[\p{Cf}\p{Cc}\p{Co}\p{Zl}\p{Zp}]|[^\S ]|  ', unicode: true);

final RegExp _invisible = RegExp(
    r'[­͏ᅟᅠ឴឵᠋-᠎⁡-⁥⁪-⁯⠀ㅤ︀-️ﾠ￹-￼]|[\u{1D173}-\u{1D17A}\u{E0000}-\u{E007F}\u{E0100}-\u{E01EF}]',
    unicode: true);

final RegExp _joinerRun = RegExp(r'[‌‍]{2,}');
final RegExp _bareOrStackedMark = RegExp(r'(?:^|\s)\p{M}|\p{M}{4}', unicode: true);
final RegExp _markAfterComposable = RegExp(
    r'[\p{Script=Latin}\p{Script=Greek}\p{Script=Cyrillic}]\p{M}',
    unicode: true);

/// The allowed zero-width joiners and emoji selectors removed, once (the same two replacements as the server).
String _withoutJoiners(String text) => text
    .replaceAll(
        RegExp(
            r'(?<=[\p{L}\p{M}\p{S}\p{N}️])[‌‍](?=[\p{L}\p{S}\p{N}])',
            unicode: true),
        '')
    .replaceAll(RegExp(r'(?<=[\p{S}\p{N}#*])️', unicode: true), '');

bool _wellFormed(String text) {
  final units = text.codeUnits;
  for (var i = 0; i < units.length; i++) {
    final u = units[i];
    if (u >= 0xD800 && u <= 0xDBFF) {
      if (i + 1 >= units.length ||
          units[i + 1] < 0xDC00 ||
          units[i + 1] > 0xDFFF) {
        return false;
      }
      i++;
    } else if (u >= 0xDC00 && u <= 0xDFFF) {
      return false;
    }
  }
  return true;
}

/// Null when [text] may be sent as a label, else what is wrong with it.
LabelProblem? labelProblem(String text) {
  if (text.isEmpty || text.trim().isEmpty) return LabelProblem.empty;
  if (!_wellFormed(text)) return LabelProblem.notAllowed;
  if (text.runes.length > labelMaxLength) return LabelProblem.tooLong;
  if (text.trim() != text) return LabelProblem.padded;
  if (_joinerRun.hasMatch(text)) return LabelProblem.notAllowed;
  if (_bareOrStackedMark.hasMatch(text)) return LabelProblem.notAllowed;
  if (_markAfterComposable.hasMatch(text)) return LabelProblem.notAllowed;
  final plain = _withoutJoiners(text);
  if (_notAllowedSpaceOrFormat.hasMatch(plain) || _invisible.hasMatch(plain)) {
    return LabelProblem.notAllowed;
  }
  return null;
}

/// [label] when it passes the rules, else null: a label the server sent that breaks them is never drawn next to a security decision.
String? displaySafeLabel(String? label) =>
    label != null && labelProblem(label) == null ? label : null;
