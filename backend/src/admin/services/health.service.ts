import { Injectable, Optional } from '@nestjs/common';
import { EventIngestionQueue } from '../../adherence/queues/event-ingestion.queue';
import { InjectConnection } from '@nestjs/typeorm';
import { Connection } from 'typeorm';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { AgentAdherenceEvent } from '../../entities/agent-adherence-event.entity';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';

/**
 * HealthService
 * 
 * Provides system health and metrics.
 */
@Injectable()
export class HealthService {
  constructor(
    @InjectConnection()
    private connection: Connection,
    @InjectRepository(AgentAdherenceEvent)
    private eventRepo: Repository<AgentAdherenceEvent>,
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
    @Optional()
    private eventIngestionQueue?: EventIngestionQueue,
  ) {}

  /**
   * Get system health status
   */
  async getSystemHealth() {
    // Check database connection
    let dbStatus = 'disconnected';
    let dbResponseTime = 0;
    try {
      const start = Date.now();
      await this.connection.query('SELECT 1');
      dbResponseTime = Date.now() - start;
      dbStatus = 'connected';
    } catch (error) {
      dbStatus = 'error';
    }

    // Get metrics
    const oneHourAgo = new Date(Date.now() - 60 * 60 * 1000);
    const oneDayAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);

    const eventsLastHour = await this.eventRepo
      .createQueryBuilder('event')
      .where('event.createdAt >= :oneHourAgo', { oneHourAgo })
      .getCount();

    const eventsLast24h = await this.eventRepo
      .createQueryBuilder('event')
      .where('event.createdAt >= :oneDayAgo', { oneDayAgo })
      .getCount();

    const activeWorkstations = await this.workstationRepo.count({
      where: { isActive: true },
    });

    // Get queue stats if queue is available
    let queueStats = null;
    let queueStatus = 'unknown';
    if (this.eventIngestionQueue) {
      try {
        queueStats = await this.eventIngestionQueue.getQueueStats();
        // Determine queue status based on depth
        if (queueStats.waiting > 10000) {
          queueStatus = 'critical';
        } else if (queueStats.waiting > 1000) {
          queueStatus = 'warning';
        } else {
          queueStatus = 'operational';
        }
      } catch (error) {
        queueStatus = 'error';
        queueStats = {
          waiting: 0,
          active: 0,
          completed: 0,
          failed: 0,
          delayed: 0,
          total: 0,
        };
      }
    }

    return {
      status: dbStatus === 'connected' ? 'healthy' : 'unhealthy',
      timestamp: new Date().toISOString(),
      services: {
        database: {
          status: dbStatus,
          response_time_ms: dbResponseTime,
        },
        redis: {
          status: 'unknown', // TODO: Check Redis connection
          response_time_ms: 0,
        },
        event_queue: {
          status: queueStatus,
          waiting: queueStats?.waiting || 0,
          active: queueStats?.active || 0,
          completed: queueStats?.completed || 0,
          failed: queueStats?.failed || 0,
          delayed: queueStats?.delayed || 0,
          total: queueStats?.total || 0,
        },
      },
      metrics: {
        events_ingested_last_hour: eventsLastHour,
        events_ingested_last_24h: eventsLast24h,
        active_workstations: activeWorkstations,
        calculations_pending: 0, // TODO: Implement calculation queue
        database_size_gb: 0, // TODO: Get from database
      },
    };
  }
}

