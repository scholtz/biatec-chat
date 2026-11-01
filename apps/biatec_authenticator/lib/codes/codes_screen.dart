import 'package:flutter/material.dart';

import '../ui/ui.dart';

/// The Codes tab's first screen. The list of two-factor accounts replaces it (T-224); until then it says what the tab is for.
class CodesScreen extends StatelessWidget {
  const CodesScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Codes')),
      body: const EmptyState(
        key: Key('codes-placeholder'),
        icon: Icons.pin_outlined,
        title: 'No codes yet',
        message: 'Two-factor codes for your accounts will appear here.',
      ),
    );
  }
}
