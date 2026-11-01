import 'package:flutter/material.dart';

import '../../theme.dart';

/// The steps of the onboarding, in order.
enum OnboardingStep {
  welcome('Welcome'),
  connect('Connect'),
  protect('Protect the key'),
  recovery('Recovery code'),
  done('Done');

  const OnboardingStep(this.label);
  final String label;

  static int get count => values.length;
}

/// Where the user is in the onboarding: a bar of segments, and the same in words ("Step 2 of 5 · Connect"), so the position never rests on color. A screen reader hears the words once.
class StepIndicator extends StatelessWidget {
  const StepIndicator({super.key, required this.step});

  final OnboardingStep step;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final text =
        'Step ${step.index + 1} of ${OnboardingStep.count} · ${step.label}';
    return Semantics(
      container: true,
      label: text,
      child: ExcludeSemantics(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              for (final s in OnboardingStep.values)
                Expanded(
                  child: Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 2),
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        color: s.index <= step.index
                            ? scheme.primary
                            : scheme.outlineVariant,
                        borderRadius: BorderRadius.circular(AppRadius.s),
                      ),
                      child: const SizedBox(height: 6),
                    ),
                  ),
                ),
            ]),
            const SizedBox(height: AppSpacing.s),
            Text(text, style: Theme.of(context).textTheme.labelLarge),
          ],
        ),
      ),
    );
  }
}
