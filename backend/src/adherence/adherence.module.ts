import { Module } from '@nestjs/common';
import { TypeOrmModule } from '@nestjs/typeorm';
import { BullModule } from '@nestjs/bullmq';
import { AgentAdherenceEvent } from '../entities/agent-adherence-event.entity';
import { AgentAdherenceSummary } from '../entities/agent-adherence-summary.entity';
import { AgentAdherenceException } from '../entities/agent-adherence-exception.entity';
import { AgentWorkstationConfiguration } from '../entities/agent-workstation-configuration.entity';
import { ApplicationClassification } from '../entities/application-classification.entity';
import { ClientWebsite } from '../entities/client-website.entity';
import { CallingApp } from '../entities/calling-app.entity';
import { EmployeePersonalInfo } from '../entities/employee-personal-info.entity';
import { AgentSchedule } from '../entities/agent-schedule.entity';
import { Employee } from '../entities/employee.entity';
import { User } from '../entities/user.entity';
import { EventsController } from './controllers/events.controller';
import { WorkstationConfigController } from './controllers/workstation-config.controller';
import { AgentStatusController } from './controllers/agent-status.controller';
import { SummariesController } from './controllers/summaries.controller';
import { ReportsController } from './controllers/reports.controller';
import { EventIngestionService } from './services/event-ingestion.service';
import { WorkstationConfigService } from './services/workstation-config.service';
import { WorkstationAuthService } from './services/workstation-auth.service';
import { AgentStatusService } from './services/agent-status.service';
import { AdherenceCalculationService } from './services/adherence-calculation.service';
import { SummariesService } from './services/summaries.service';
import { ReportingService } from './services/reporting.service';
import { EventIngestionQueue } from './queues/event-ingestion.queue';
import { EventIngestionProcessor } from './queues/event-ingestion.processor';
import { AdherenceCalculationScheduler } from './schedulers/adherence-calculation.scheduler';
import { PartitionManagementScheduler } from './schedulers/partition-management.scheduler';

/**
 * AdherenceModule
 * 
 * Handles:
 * - Event ingestion from Desktop Agents
 * - Workstation configuration
 * - Adherence calculation ✅
 * - Adherence summaries ✅
 * - Scheduled jobs (daily calculation, partition management) ✅
 * 
 * Note: Rate limiting is configured globally in AppModule.
 * Per-workstation rate limiting (10 req/min) is implemented
 * using WorkstationRateLimitGuard with Redis-based storage.
 */
@Module({
  imports: [
    // BullMQ Queue Configuration
    BullModule.registerQueue({
      name: 'event-ingestion',
    }),
    TypeOrmModule.forFeature([
      AgentAdherenceEvent,
      AgentAdherenceSummary,
      AgentAdherenceException,
      AgentWorkstationConfiguration,
      ApplicationClassification,
      ClientWebsite, // For client website detection
      CallingApp, // For calling app detection
      EmployeePersonalInfo, // For NT account resolution
      AgentSchedule, // For break schedule queries
      Employee, // For adherence relationships
      User, // For exception relationships
    ]),
  ],
  controllers: [
    EventsController,
    WorkstationConfigController,
    AgentStatusController,
    SummariesController,
    ReportsController,
  ],
  providers: [
    EventIngestionService,
    WorkstationConfigService,
    WorkstationAuthService,
    AgentStatusService,
    AdherenceCalculationService,
    SummariesService,
    ReportingService,
    EventIngestionQueue,
    EventIngestionProcessor,
    AdherenceCalculationScheduler,
    PartitionManagementScheduler,
  ],
  exports: [WorkstationAuthService, EventIngestionQueue], // Export for use in guards and health monitoring
})
export class AdherenceModule {}

/**
 * Note on Rate Limiting:
 * 
 * Global rate limiting is configured in AppModule (1000 req/min).
 * Per-workstation rate limiting (10 req/min per workstation) is implemented
 * using WorkstationRateLimitGuard with Redis-based storage.
 * 
 * Rate limiting order:
 * 1. WorkstationAuthGuard - validates API key and workstation ID
 * 2. WorkstationRateLimitGuard - enforces per-workstation rate limit (10 req/min)
 * 3. ThrottlerGuard - enforces global rate limit (1000 req/min)
 */

