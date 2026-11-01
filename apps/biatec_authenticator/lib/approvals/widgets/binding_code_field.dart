import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../theme.dart';
import 'mono.dart';

/// Where the user types the binding code: the six digits shown on the screen where they started the action. It never comes from this request, so it proves the person holding the phone is the one who started it. Large and fixed-width, with a reminder never to enter a code someone else sends.
class BindingCodeField extends StatelessWidget {
  const BindingCodeField({
    super.key,
    required this.controller,
    required this.enabled,
    required this.attemptsRemaining,
  });

  final TextEditingController controller;
  final bool enabled;

  /// As the server last reported, or null before the first wrong code.
  final int? attemptsRemaining;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      TextField(
        key: const Key('binding-code'),
        controller: controller,
        enabled: enabled,
        keyboardType: TextInputType.number,
        textAlign: TextAlign.center,
        maxLength: 6,
        inputFormatters: [FilteringTextInputFormatter.digitsOnly],
        // The code is a secret for the length of one request: the keyboard does not learn it.
        autocorrect: false,
        enableSuggestions: false,
        enableIMEPersonalizedLearning: false,
        style: theme.textTheme.headlineMedium
            ?.merge(monoStyle)
            .copyWith(letterSpacing: 8),
        decoration: InputDecoration(
          labelText: 'Code shown where you started this action',
          helperText: switch (attemptsRemaining) {
            null => 'Three attempts in total',
            1 => '1 attempt left',
            final n => '$n attempts left',
          },
          helperMaxLines: 2,
          counterText: '',
        ),
      ),
      const SizedBox(height: AppSpacing.xs),
      Text(
        'It must match the screen where you started this action. Never enter '
        'a code that someone sends you.',
        style: theme.textTheme.bodySmall
            ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
      ),
    ]);
  }
}
