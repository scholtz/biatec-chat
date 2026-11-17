import 'package:material_ui/material_ui.dart';

import '../../theme.dart';
import '../onboarding_texts.dart';

/// The steps of the onboarding, in order.
enum OnboardingStep {
  welcome,
  connect,
  protect,
  recovery,
  done;

  /// The name of the step in English.
  String get label => labelIn(OnboardingTexts.en);

  /// The name of the step in the language of [texts].
  String labelIn(OnboardingTexts texts) => texts.text('step_$name');

  static int get count => values.length;
}

/// Where the user is in the onboarding: a bar of segments, and the same in words ("Step 2 of 5 · Connect"), so the position never rests on color. A screen reader hears the words once.
class StepIndicator extends StatelessWidget {
  const StepIndicator({super.key, required this.step});

  final OnboardingStep step;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final t = OnboardingTexts.of(Localizations.maybeLocaleOf(context));
    final text = t.text('stepLine', {
      'n': step.index + 1,
      'count': OnboardingStep.count,
      'label': step.labelIn(t),
    });
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
