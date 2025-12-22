import { Injectable, Logger } from '@nestjs/common';
import { Cron } from '@nestjs/schedule';
import { InjectConnection } from '@nestjs/typeorm';
import { Connection } from 'typeorm';

/**
 * PartitionManagementScheduler
 * 
 * Scheduled jobs for partition lifecycle management.
 * - Creates next month's partition on the 1st of each month
 * - Detaches partitions older than 3 months on the 1st of each month
 */
@Injectable()
export class PartitionManagementScheduler {
  private readonly logger = new Logger(PartitionManagementScheduler.name);

  constructor(
    @InjectConnection()
    private connection: Connection,
  ) {}

  /**
   * Monthly partition creation job
   * 
   * Runs on the 1st of each month at 00:00 UTC.
   * Creates partition for next month.
   * 
   * Cron: 0 0 1 * * (00:00 UTC on 1st of month)
   */
  @Cron('0 0 1 * *', {
    name: 'create-monthly-partition',
    timeZone: 'UTC',
  })
  async handlePartitionCreation() {
    this.logger.log('Starting monthly partition creation job');

    try {
      // Calculate next month
      const now = new Date();
      const nextMonth = new Date(now.getFullYear(), now.getMonth() + 1, 1);
      const year = nextMonth.getFullYear();
      const month = String(nextMonth.getMonth() + 1).padStart(2, '0');
      const partitionName = `agent_adherence_events_${year}_${month}`;

      // Calculate start and end dates for partition
      const startDate = new Date(year, nextMonth.getMonth(), 1);
      const endDate = new Date(year, nextMonth.getMonth() + 1, 1);

      // Call partition creation function
      await this.connection.query(
        `SELECT create_monthly_partition(
          'agent_adherence_events',
          $1,
          $2::date,
          $3::date
        )`,
        [partitionName, startDate.toISOString().split('T')[0], endDate.toISOString().split('T')[0]],
      );

      this.logger.log(`Successfully created partition: ${partitionName}`);
    } catch (error) {
      this.logger.error(
        `Monthly partition creation job failed: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
    }
  }

  /**
   * Monthly partition detachment job
   * 
   * Runs on the 1st of each month at 01:00 UTC (after partition creation).
   * Detaches partitions older than 3 months.
   * 
   * Cron: 0 1 1 * * (01:00 UTC on 1st of month)
   */
  @Cron('0 1 1 * *', {
    name: 'detach-old-partitions',
    timeZone: 'UTC',
  })
  async handlePartitionDetachment() {
    this.logger.log('Starting monthly partition detachment job');

    try {
      // Call partition detachment function (detaches partitions older than 3 months)
      const result = await this.connection.query(
        `SELECT detach_old_partitions('agent_adherence_events', 3)`,
      );

      this.logger.log(`Partition detachment completed. Result: ${JSON.stringify(result)}`);
    } catch (error) {
      this.logger.error(
        `Monthly partition detachment job failed: ${error instanceof Error ? error.message : String(error)}`,
        error instanceof Error ? error.stack : undefined,
      );
    }
  }
}

