import { Injectable, Logger } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository, SelectQueryBuilder, Between, LessThanOrEqual, MoreThanOrEqual } from 'typeorm';
import { AgentAdherenceSummary } from '../../entities/agent-adherence-summary.entity';
import { Employee } from '../../entities/employee.entity';
import { CacheService } from '../../common/cache.service';

/**
 * Query DTO for summaries
 */
export interface SummariesQueryDto {
  employeeId?: string;
  startDate?: string; // YYYY-MM-DD
  endDate?: string; // YYYY-MM-DD
  department?: string;
  minAdherence?: number;
  maxAdherence?: number;
  page?: number;
  limit?: number;
}

/**
 * Paginated response
 */
export interface PaginatedResponse<T> {
  data: T[];
  pagination: {
    page: number;
    limit: number;
    total: number;
    totalPages: number;
  };
}

/**
 * SummariesService
 * 
 * Handles querying adherence summaries with filters.
 * Used by Adherence Backend for admin-facing endpoints.
 */
@Injectable()
export class SummariesService {
  private readonly logger = new Logger(SummariesService.name);
  private readonly DEFAULT_PAGE = 1;
  private readonly DEFAULT_LIMIT = 50;
  private readonly MAX_LIMIT = 500;

  constructor(
    @InjectRepository(AgentAdherenceSummary)
    private summaryRepo: Repository<AgentAdherenceSummary>,
    @InjectRepository(Employee)
    private employeeRepo: Repository<Employee>,
    private cacheService: CacheService,
  ) {}

  /**
   * Get summaries with filters and pagination
   * Uses Redis caching for performance
   */
  async getSummaries(query: SummariesQueryDto): Promise<PaginatedResponse<any>> {
    const page = Math.max(1, query.page || this.DEFAULT_PAGE);
    const limit = Math.min(this.MAX_LIMIT, Math.max(1, query.limit || this.DEFAULT_LIMIT));
    const skip = (page - 1) * limit;

    // Determine TTL based on date range
    // Today's data: 5 minutes (changes frequently)
    // Historical data: 15 minutes (rarely changes)
    const today = new Date().toISOString().split('T')[0];
    const isTodayData = query.startDate === today || query.endDate === today || 
                       (!query.startDate && !query.endDate);
    const ttlSeconds = isTodayData ? 300 : 900; // 5 min or 15 min

    return this.cacheService.getOrSet(
      'summaries',
      'list',
      query,
      async () => {
        // Build query builder
        const qb = this.summaryRepo
          .createQueryBuilder('summary')
          .leftJoinAndSelect('summary.employee', 'employee')
          .orderBy('summary.scheduleDate', 'DESC')
          .addOrderBy('employee.fullNameEn', 'ASC');

        // Apply filters
        this.applyFilters(qb, query);

        // Get total count
        const total = await qb.getCount();

        // Apply pagination
        const summaries = await qb.skip(skip).take(limit).getMany();

        // Transform to response format
        const data = summaries.map((summary) => this.transformSummary(summary));

        return {
          data,
          pagination: {
            page,
            limit,
            total,
            totalPages: Math.ceil(total / limit),
          },
        };
      },
      ttlSeconds,
    );
  }

  /**
   * Get a single summary by ID
   * Uses Redis caching for performance
   */
  async getSummaryById(id: string): Promise<any> {
    return this.cacheService.getOrSet(
      'summaries',
      'byId',
      { id },
      async () => {
        const summary = await this.summaryRepo.findOne({
          where: { id },
          relations: ['employee'],
        });

        if (!summary) {
          return null;
        }

        return this.transformSummary(summary);
      },
      900, // 15 minutes TTL
    );
  }

  /**
   * Apply filters to query builder
   */
  private applyFilters(
    qb: SelectQueryBuilder<AgentAdherenceSummary>,
    query: SummariesQueryDto,
  ): void {
    // Employee filter
    if (query.employeeId) {
      qb.andWhere('summary.employeeId = :employeeId', { employeeId: query.employeeId });
    }

    // Date range filter
    if (query.startDate) {
      qb.andWhere('summary.scheduleDate >= :startDate', {
        startDate: query.startDate,
      });
    }
    if (query.endDate) {
      qb.andWhere('summary.scheduleDate <= :endDate', {
        endDate: query.endDate,
      });
    }

    // Department filter - use EXISTS subquery for better performance
    if (query.department) {
      qb.andWhere(
        `EXISTS (
          SELECT 1 FROM employees e
          INNER JOIN departments d ON d.id = e.department_id
          WHERE e.id = summary.employee_id AND d.name = :department
        )`,
        { department: query.department },
      );
    }

    // Adherence percentage filters
    if (query.minAdherence !== undefined) {
      qb.andWhere('summary.adherencePercentage >= :minAdherence', {
        minAdherence: query.minAdherence,
      });
    }
    if (query.maxAdherence !== undefined) {
      qb.andWhere('summary.adherencePercentage <= :maxAdherence', {
        maxAdherence: query.maxAdherence,
      });
    }
  }

  /**
   * Transform summary entity to response format
   */
  private transformSummary(summary: AgentAdherenceSummary): any {
    // Handle scheduleDate - PostgreSQL date type returns string, not Date object
    const scheduleDateStr = 
      summary.scheduleDate instanceof Date 
        ? summary.scheduleDate.toISOString().split('T')[0]
        : typeof summary.scheduleDate === 'string'
        ? summary.scheduleDate
        : String(summary.scheduleDate);

    return {
      id: summary.id,
      employeeId: summary.employeeId,
      employeeName: summary.employee?.fullNameEn || null,
      hrId: summary.employee?.hrId || null,
      scheduleDate: scheduleDateStr,
      scheduledStartTime: summary.scheduledStartTime,
      scheduledEndTime: summary.scheduledEndTime,
      scheduledDurationMinutes: summary.scheduledDurationMinutes,
      actualStartTime: summary.actualStartTime?.toISOString() || null,
      actualEndTime: summary.actualEndTime?.toISOString() || null,
      actualDurationMinutes: summary.actualDurationMinutes,
      startVarianceMinutes: summary.startVarianceMinutes,
      endVarianceMinutes: summary.endVarianceMinutes,
      breakCompliancePercentage: summary.breakCompliancePercentage
        ? parseFloat(summary.breakCompliancePercentage.toString())
        : null,
      missedBreaksCount: summary.missedBreaksCount,
      extendedBreaksCount: summary.extendedBreaksCount,
      productiveTimeMinutes: summary.productiveTimeMinutes,
      idleTimeMinutes: summary.idleTimeMinutes,
      awayTimeMinutes: summary.awayTimeMinutes,
      nonWorkAppTimeMinutes: summary.nonWorkAppTimeMinutes,
      adherencePercentage: summary.adherencePercentage
        ? parseFloat(summary.adherencePercentage.toString())
        : null,
      calculatedAt: summary.calculatedAt?.toISOString() || null,
    };
  }
}

