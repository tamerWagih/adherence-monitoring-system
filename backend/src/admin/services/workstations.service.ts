import { Injectable, ConflictException, NotFoundException, BadRequestException } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository, DataSource } from 'typeorm';
import { AgentWorkstationConfiguration } from '../../entities/agent-workstation-configuration.entity';
import { RegisterWorkstationDto } from '../../dto/register-workstation.dto';
import { WorkstationAuthService } from '../../adherence/services/workstation-auth.service';
import { randomUUID } from 'crypto';

/**
 * WorkstationsService
 * 
 * Handles workstation registration and management.
 */
@Injectable()
export class WorkstationsService {
  constructor(
    @InjectRepository(AgentWorkstationConfiguration)
    private workstationRepo: Repository<AgentWorkstationConfiguration>,
    private workstationAuthService: WorkstationAuthService,
    private dataSource: DataSource,
  ) {}

  /**
   * List all workstations with filters
   */
  async listWorkstations(query: any) {
    const { status, employee_id, department, page = 1, limit = 50 } = query;

    const queryBuilder = this.workstationRepo.createQueryBuilder('w');

    if (status === 'ACTIVE') {
      queryBuilder.where('w.isActive = :isActive', { isActive: true });
    } else if (status === 'INACTIVE') {
      queryBuilder.where('w.isActive = :isActive', { isActive: false });
    }

    if (employee_id) {
      queryBuilder.andWhere('w.employeeId = :employeeId', { employeeId: employee_id });
    }

    const skip = (page - 1) * limit;
    const [data, total] = await queryBuilder
      .skip(skip)
      .take(limit)
      .orderBy('w.createdAt', 'DESC')
      .getManyAndCount();

    return {
      data: data.map((w) => ({
        id: w.id,
        workstation_id: w.workstationId,
        employee_id: w.employeeId,
        workstation_name: w.workstationName,
        os_version: w.osVersion,
        agent_version: w.agentVersion,
        is_active: w.isActive,
        last_seen_at: w.lastSeenAt,
        last_sync_at: w.lastSyncAt,
        last_event_at: w.lastEventAt,
        registration_source: w.registrationSource,
        created_at: w.createdAt,
      })),
      pagination: {
        page: Number(page),
        limit: Number(limit),
        total,
        total_pages: Math.ceil(total / limit),
      },
    };
  }

  /**
   * Get registration status dashboard data
   */
  async getRegistrationStatus(query: any) {
    // TODO: Implement with employee joins for full dashboard data
    const total = await this.workstationRepo.count();
    const registered = await this.workstationRepo.count({
      where: { isActive: true },
    });
    const offline = await this.workstationRepo.count({
      where: {
        isActive: true,
        lastSeenAt: null, // Or check if last_seen_at > 24 hours
      },
    });

    return {
      summary: {
        total_agents: total, // TODO: Get from employees table
        registered_agents: registered,
        unregistered_agents: total - registered,
        offline_agents: offline,
        online_agents: registered - offline,
      },
      data: [], // TODO: Implement full list with employee info
      pagination: {
        page: 1,
        limit: 50,
        total: 0,
        total_pages: 0,
      },
    };
  }

  /**
   * Register a new workstation
   */
  async registerWorkstation(dto: RegisterWorkstationDto) {
    // Validate that employee exists and is active in employees table
    // Use explicit UUID casting to handle any format issues
    const employee = await this.dataSource.query(
      'SELECT id, status FROM employees WHERE id = $1::uuid',
      [dto.employee_id],
    );

    if (!employee || employee.length === 0) {
      // Try to get some sample employee IDs for debugging
      const sampleEmployees = await this.dataSource.query(
        'SELECT id::text as id, status FROM employees WHERE status = $1 LIMIT 3',
        ['Active'],
      );
      
      throw new BadRequestException(
        `Employee with ID ${dto.employee_id} does not exist in the employees table.` +
        (sampleEmployees.length > 0 
          ? ` Sample active employee IDs: ${sampleEmployees.map(e => e.id).join(', ')}`
          : ' No active employees found in database.'),
      );
    }

    // Check if employee is active
    if (employee[0].status !== 'Active') {
      throw new BadRequestException(
        `Cannot register workstation for employee with ID ${dto.employee_id}. ` +
        `Employee status is '${employee[0].status}'. Only active employees can have workstations registered.`,
      );
    }

    // Check if employee already has an active workstation
    const existing = await this.workstationRepo.findOne({
      where: { employeeId: dto.employee_id, isActive: true },
    });

    if (existing) {
      throw new ConflictException(
        'Employee already has an active workstation. Revoke existing one first.',
      );
    }

    // Generate credentials
    const workstationId = randomUUID();
    const apiKey = this.workstationAuthService.generateApiKey();
    const apiKeyHash = await this.workstationAuthService.hashApiKey(apiKey);

    // Create workstation
    const workstation = this.workstationRepo.create({
      workstationId,
      employeeId: dto.employee_id,
      apiKeyHash,
      workstationName: dto.workstation_name,
      osVersion: dto.os_version,
      agentVersion: dto.agent_version,
      isActive: true,
      registrationSource: 'ADMIN',
    });

    await this.workstationRepo.save(workstation);

    // Return credentials (shown once only)
    return {
      workstation_id: workstationId,
      api_key: apiKey, // Plain text - shown once only
      message: 'Workstation registered successfully',
      warning: 'These credentials will not be shown again. Store them securely.',
    };
  }

  /**
   * Revoke/deactivate workstation
   */
  async revokeWorkstation(workstationId: string, reason?: string) {
    const workstation = await this.workstationRepo.findOne({
      where: { workstationId },
    });

    if (!workstation) {
      throw new NotFoundException('Workstation not found');
    }

    workstation.isActive = false;
    await this.workstationRepo.save(workstation);

    return {
      message: 'Workstation deactivated successfully',
      workstation_id: workstationId,
    };
  }
}

