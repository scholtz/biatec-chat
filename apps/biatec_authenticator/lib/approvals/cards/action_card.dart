import 'package:material_ui/material_ui.dart';

import '../../theme.dart';
import '../consent_request.dart';
import '../widgets/fact_row.dart';
import 'relying_party_card.dart';

/// `biatec_action`: the title and the labelled lines, all of them written by the service, inside the bordered area headed "Written by the service". Nothing the service did not list is shown.
class ActionCardBody extends StatelessWidget {
  const ActionCardBody({super.key, required this.detail});

  final ActionDetail detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return WrittenByServiceBox(children: [
      Semantics(
        header: true,
        child: Text(detail.title,
            key: const Key('action-title'),
            style: theme.textTheme.titleLarge
                ?.copyWith(fontWeight: FontWeight.w700)),
      ),
      const SizedBox(height: AppSpacing.xs),
      for (var i = 0; i < detail.lines.length; i++)
        Fact(detail.lines[i].$1, detail.lines[i].$2,
            key: Key('action-line-$i')),
    ]);
  }
}
