import 'package:flutter/material.dart';

import '../../theme.dart';
import '../../ui/ui.dart';

/// Shows the recovery code once. Moving on needs an explicit confirmation that it was saved: the button stays off until the box is ticked.
class RecoveryCodeView extends StatefulWidget {
  const RecoveryCodeView(
      {super.key, required this.code, required this.onSaved, this.error});

  final String code;
  final VoidCallback onSaved;

  /// Why the last confirmation did not go through (the code is still shown), or null.
  final String? error;

  @override
  State<RecoveryCodeView> createState() => _RecoveryCodeViewState();
}

class _RecoveryCodeViewState extends State<RecoveryCodeView> {
  bool _written = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Semantics(
          header: true,
          child: Text('Recovery code', style: theme.textTheme.titleLarge),
        ),
        const SizedBox(height: AppSpacing.s),
        const Text('Write this code down and keep it safe. It activates a '
            'replacement device immediately.'),
        const SizedBox(height: AppSpacing.m),
        const StatusBanner(
          severity: StatusSeverity.warning,
          title: 'Shown only once',
          message: 'You cannot see this code again after you continue.',
        ),
        const SizedBox(height: AppSpacing.m),
        // Not selectable on purpose: a copied recovery code lands in the clipboard, which other apps and keyboards read (BCHAT-031).
        // A screen reader reads it character by character.
        Semantics(
          label:
              'Recovery code: ${widget.code.split('').map((c) => c == '-' ? 'dash' : c).join(' ')}',
          excludeSemantics: true,
          child: Text(widget.code,
              textAlign: TextAlign.center,
              style: theme.textTheme.headlineMedium
                  ?.copyWith(fontFamily: 'monospace')),
        ),
        if (widget.error != null) ...[
          const SizedBox(height: AppSpacing.m),
          StatusBanner(
            severity: StatusSeverity.danger,
            title: 'Could not confirm',
            message: '${widget.error} Your code is still shown. Try again.',
          ),
        ],
        const SizedBox(height: AppSpacing.m),
        CheckboxListTile(
          key: const Key('recovery-confirm'),
          value: _written,
          onChanged: (v) => setState(() => _written = v ?? false),
          controlAffinity: ListTileControlAffinity.leading,
          contentPadding: EdgeInsets.zero,
          title: const Text(
              'I have written the code down and keep it somewhere safe.'),
        ),
        const SizedBox(height: AppSpacing.s),
        FilledButton(
          key: const Key('recovery-saved'),
          onPressed: _written ? widget.onSaved : null,
          child: const Text('I have saved it'),
        ),
      ],
    );
  }
}
