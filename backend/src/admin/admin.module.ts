import { Module } from '@nestjs/common';
import { TypeOrmModule } from '@nestjs/typeorm';
import { AgentWorkstationConfiguration } from '../entities/agent-workstation-configuration.entity';
import { AgentAdherenceEvent } from '../entities/agent-adherence-event.entity';
import { WorkstationsController } from './controllers/workstations.controller';
import { HealthController } from './controllers/health.controller';
import { WorkstationsService } from './services/workstations.service';
import { HealthService } from './services/health.service';
import { AdherenceModule } from '../adherence/adherence.module';

/**
 * AdminModule
 * 
 * Handles:
 * - Device/workstation management
 * - Agent sync status
 * - System health monitoring
 * - Configuration management
 */
@Module({
  imports: [
    TypeOrmModule.forFeature([
      AgentWorkstationConfiguration,
      AgentAdherenceEvent,
    ]),
    AdherenceModule, // Import to use WorkstationAuthService and EventIngestionQueue (exported)
  ],
  controllers: [WorkstationsController, HealthController],
  providers: [WorkstationsService, HealthService],
})
export class AdminModule {}

