import 'package:flutter/material.dart';

import '../theme.dart';

/// Needs a parent with a bounded height (a screen body); inside a `ListView` use the widget without it. Centers [child] in the space it is given, in a column no wider than [kMaxContentWidth], and scrolls when the content is taller than that space (large text, a phone on its side), so a button at the bottom stays reachable.
class ScrollableCenter extends StatelessWidget {
  const ScrollableCenter({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, box) => SingleChildScrollView(
        child: ConstrainedBox(
          constraints: BoxConstraints(
              minHeight: box.hasBoundedHeight ? box.maxHeight : 0),
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: kMaxContentWidth),
              child: Padding(
                padding: const EdgeInsets.all(AppSpacing.l),
                child: child,
              ),
            ),
          ),
        ),
      ),
    );
  }
}
