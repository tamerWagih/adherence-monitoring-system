import { Injectable, Logger } from '@nestjs/common';
import { Cron } from '@nestjs/schedule';
import { AdherenceCalculationService } from '../services/adherence-calculation.service';
import { CacheService } from '../../common/cache.service';

/**
 * AdherenceCalculationScheduler
 * 
 * Scheduled jobs for daily adherence calculation.
 * Runs at 00:00 Egypt time (22:00 UTC previous day) to calculate adherence for the previous day.
 */
@Injectable()
export class AdherenceCalculationScheduler {
  private readonly logger = new Logger(AdherenceCalculationScheduler.name);

  constructor(
    private adherenceCalculationService: AdherenceCalculationService,
    private cacheService: CacheService,
  ) {}

  /**
   * Daily adherence calculation job
   * 
   * Runs at 00:00 Egypt time (22:00 UTC previous day).
   * Calculates adherence for all employees with schedules for yesterday.
   * 
   * Cron: 0 22 * * * (22:00 UTC = 00:00 Egypt time next day)
   */
  @Cron('0 22 * * *', {
    name: 'daily-adherence-calculation',
    timeZone: 'UTC',
  })
  async handleDailyCalculation() {
    this.logger.log('Starting daily adherence calculation job');

    try {
      // Calculate for yesterday in Egypt timezone
      const now = new Date();
      const egyptOffset = 2 * 60; // +02:00 in minutes
      const utcTime = now.getTime() + now.getTimezoneOffset() * 60000;
      const egyptTime = new Date(utcTime + egyptOffset * 60000);
      egyptTime.setDate(egyptTime.getDate() - 1); // Yesterday

      const result = await this.adherenceCalculationService.batchCalculateAdherence(
        egyptTime,
        50, // Default batch size
      );

      this.logger.log(
        `Daily adherence calculation completed: ${result.processed} processed, ${result.failed} failed`,
      );

      if (result.failed > 0) {
        this.logger.warn(
          `Daily adherence calculation had ${result.failed} failures. Errors: ${JSON.stringify(result.errors)}`,
        );
      }

      // Invalidate cache for the calculated date
      const dateStr = egyptTime.toISOString().split('T')[0];
      this.logger.log(`Invalidating cache for date: ${dateStr}`);
      
      // Invalidate summaries and reports for this date
      await Promise.all([
        this.cacheService.invalidatePattern(`summaries:*`),
        this.cacheService.invalidatePattern(`reports:*`),
        this.cacheService.invalidatePattern(`adherence-read:*`),
        this.cacheService.invalidatePattern(`agent-status:*`),
      ]);

      this.logger.log('Cache invalidation completed after daily calculation');
    } catch (error) {
      this.logger.error(
        `Daily adherence calculation job failed: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
    }
  }
}

