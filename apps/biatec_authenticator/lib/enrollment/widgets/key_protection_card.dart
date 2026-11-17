import 'package:material_ui/material_ui.dart';

import '../../keys/key_security_level.dart';
import '../../ui/ui.dart';
import '../enrollment_strings.dart';
import '../onboarding_texts.dart';

/// Where the device key is kept, in words and with an icon (never by color alone).
class KeyProtectionCard extends StatelessWidget {
  const KeyProtectionCard({super.key, required this.level, this.strings});

  final KeySecurityLevel level;

  /// The texts of the warning; the language of the screen when null.
  final EnrollStrings? strings;

  @override
  Widget build(BuildContext context) {
    final locale = Localizations.maybeLocaleOf(context);
    final t = OnboardingTexts.of(locale);
    if (level.isHardware) {
      return StatusBanner(
        severity: StatusSeverity.success,
        icon: Icons.verified_user_outlined,
        title: t.text('hwTitle'),
        message: t.text('hwMessage', {'level': t.levelLabel(level)}),
      );
    }
    if (level == KeySecurityLevel.windowsAccount) {
      // A Windows computer without a usable TPM: enrolled, and the user is told what that means and what would make it safer.
      final strings = this.strings ?? EnrollStrings.of(locale);
      return StatusBanner(
        key: const Key('software-key-warning'),
        severity: StatusSeverity.warning,
        icon: Icons.gpp_maybe_outlined,
        title: strings.softwareKeyTitle,
        message: '${strings.softwareKey}\n\n${strings.softwareKeyTips}',
      );
    }
    if (level == KeySecurityLevel.software) {
      return StatusBanner(
        severity: StatusSeverity.danger,
        icon: Icons.gpp_bad_outlined,
        title: t.text('swTitle'),
        message: t.text('swMessage', {'level': t.levelLabel(level)}),
      );
    }
    return StatusBanner(
      severity: StatusSeverity.warning,
      icon: Icons.help_outline,
      title: t.text('unknownTitle'),
      message: t.text('unknownMessage'),
    );
  }
}
