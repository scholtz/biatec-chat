import 'package:flutter/material.dart';

/// A fixed-width face for things compared character by character (addresses, hashes, the binding code). `monospace` is a real family on Android only; the others are the fixed-width faces the other platforms ship, so "I", "l", "O" and "0" stay apart everywhere.
const TextStyle monoStyle = TextStyle(
  fontFamily: 'monospace',
  fontFamilyFallback: ['Menlo', 'Consolas', 'Courier New', 'Courier'],
);
