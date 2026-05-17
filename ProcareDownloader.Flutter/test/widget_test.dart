import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  testWidgets('Widget test scaffold', (WidgetTester tester) async {
    await tester.pumpWidget(const Center());

    expect(find.byType(Center), findsOneWidget);
  });
}
