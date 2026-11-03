import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/ai_service.dart';

void main() {
  group('ReviewStatistics.fromJson', () {
    test('reads camelCase including the background queue depth', () {
      final stats = ReviewStatistics.fromJson({
        'totalNeedingReview': 7,
        'uncategorizedCount': 3,
        'lowConfidenceCount': 1,
        'aiEligibleCount': 4,
        'queuedForAiCount': 6,
        'thisMonthCount': 5,
        'olderCount': 2,
      });

      expect(stats.totalNeedingReview, 7);
      expect(stats.aiEligibleCount, 4);
      expect(stats.queuedForAiCount, 6);
      expect(stats.thisMonthCount, 5);
      expect(stats.olderCount, 2);
    });

    test('reads PascalCase', () {
      final stats = ReviewStatistics.fromJson({
        'TotalNeedingReview': 2,
        'UncategorizedCount': 1,
        'LowConfidenceCount': 0,
        'AiEligibleCount': 1,
        'QueuedForAiCount': 9,
      });

      expect(stats.totalNeedingReview, 2);
      expect(stats.queuedForAiCount, 9);
    });

    test('missing queue depth defaults to zero (older backend)', () {
      final stats = ReviewStatistics.fromJson({
        'totalNeedingReview': 1,
        'uncategorizedCount': 1,
        'lowConfidenceCount': 0,
      });

      expect(stats.queuedForAiCount, 0);
      expect(stats.aiEligibleCount, 0);
    });
  });
}
