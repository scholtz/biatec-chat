import 'dart:async';

import 'package:material_ui/material_ui.dart';

import '../../theme.dart';
import '../../ui/ui.dart';
import '../onboarding_texts.dart';

/// Shows the recovery code once. Moving on needs an explicit confirmation that it was saved: the button stays off until the box is ticked.
class RecoveryCodeView extends StatefulWidget {
  const RecoveryCodeView(
      {super.key, required this.code, required this.onSaved, this.error});

  final String code;

  /// May return a future: the button stays off until it completes, so a double tap confirms once.
  final FutureOr<void> Function() onSaved;

  /// Why the last confirmation did not go through (the code is still shown), or null.
  final String? error;

  @override
  State<RecoveryCodeView> createState() => _RecoveryCodeViewState();
}

class _RecoveryCodeViewState extends State<RecoveryCodeView> {
  bool _written = false;
  bool _confirming = false;

  Future<void> _save() async {
    setState(() => _confirming = true);
    try {
      await widget.onSaved();
    } finally {
      if (mounted) setState(() => _confirming = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final t = OnboardingTexts.of(Localizations.maybeLocaleOf(context));
    final error = widget.error;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Semantics(
          header: true,
          child: Text(t.text('rcTitle'), style: theme.textTheme.titleLarge),
        ),
        const SizedBox(height: AppSpacing.s),
        Text(t.text('rcIntro')),
        const SizedBox(height: AppSpacing.m),
        StatusBanner(
          severity: StatusSeverity.warning,
          title: t.text('rcOnceTitle'),
          message: t.text('rcOnce'),
        ),
        const SizedBox(height: AppSpacing.m),
        // Not selectable on purpose: a copied recovery code lands in the clipboard, which other apps and keyboards read (BCHAT-031).
        // A screen reader reads it character by character.
        Semantics(
          label: t.text('rcSemantics', {
            'chars': widget.code
                .split('')
                .map((c) => c == '-' ? t.text('rcDash') : c)
                .join(' '),
          }),
          excludeSemantics: true,
          child: Text(widget.code,
              textAlign: TextAlign.center,
              style: theme.textTheme.headlineMedium
                  ?.copyWith(fontFamily: 'monospace')),
        ),
        if (error != null) ...[
          const SizedBox(height: AppSpacing.m),
          StatusBanner(
            severity: StatusSeverity.danger,
            title: t.text('rcConfirmFailedTitle'),
            message: t.text('rcConfirmFailed', {'why': t.message(error)}),
          ),
        ],
        const SizedBox(height: AppSpacing.m),
        CheckboxListTile(
          key: const Key('recovery-confirm'),
          value: _written,
          onChanged: (v) => setState(() => _written = v ?? false),
          controlAffinity: ListTileControlAffinity.leading,
          contentPadding: EdgeInsets.zero,
          title: Text(t.text('rcWritten')),
        ),
        const SizedBox(height: AppSpacing.s),
        FilledButton(
          key: const Key('recovery-saved'),
          onPressed: _written && !_confirming ? _save : null,
          child: Text(t.text('rcSaved')),
        ),
      ],
    );
  }
}
