import { Injectable } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository } from 'typeorm';
import { ApplicationClassification } from '../../entities/application-classification.entity';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';

/**
 * WorkstationConfigService
 * 
 * Provides configuration data to Desktop Agents.
 */
@Injectable()
export class WorkstationConfigService {
  constructor(
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
    @InjectRepository(ApplicationClassification)
    private classificationRepo: Repository<ApplicationClassification>,
  ) {}

  /**
   * Get workstation configuration
   */
  async getWorkstationConfig(workstationId: string) {
    const workstation = await this.workstationRepo.findOne({
      where: { workstationId },
    });

    if (!workstation) {
      throw new Error('Workstation not found');
    }

    // Get active application classifications
    const classifications = await this.classificationRepo.find({
      where: { isActive: true },
      order: { priority: 'DESC' },
    });

    return {
      workstation_id: workstationId,
      sync_interval_seconds: 60, // Default, can be configured per workstation
      batch_size: 100, // Default batch size
      idle_threshold_minutes: 5, // Default idle threshold
      application_classifications: classifications.map((c) => ({
        name_pattern: c.namePattern,
        path_pattern: c.pathPattern,
        window_title_pattern: c.windowTitlePattern,
        classification: c.classification,
        priority: c.priority,
      })),
      break_schedules: [], // TODO: Load from agent_schedules table
    };
  }
}

