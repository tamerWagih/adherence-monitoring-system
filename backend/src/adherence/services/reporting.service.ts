import { Injectable, Logger } from '@nestjs/common';
import { InjectRepository } from '@nestjs/typeorm';
import { Repository, SelectQueryBuilder } from 'typeorm';
import { AgentAdherenceSummary } from '../../entities/agent-adherence-summary.entity';
import { Employee } from '../../entities/employee.entity';

/**
 * Daily Report Response
 */
export interface DailyReportResponse {
  reportDate: string;
  totalAgents: number;
  agentsWithSchedule: number;
  agentsWithData: number;
  averageAdherence: number;
  agentsByAdherenceRange: {
    '90-100': number;
    '80-89': number;
    '70-79': number;
    '60-69': number;
    '0-59': number;
  };
  summary: Array<{
    employeeId: string;
    employeeName: string;
    hrId: number | null;
    department?: string;
    adherencePercentage: number;
    scheduledMinutes: number;
    actualMinutes: number;
  }>;
}

/**
 * Weekly Report Response
 */
export interface WeeklyReportResponse {
  weekStart: string;
  weekEnd: string;
  totalAgents: number;
  averageAdherence: number;
  dailyAverages: Array<{
    date: string;
    averageAdherence: number;
    agentsWithData: number;
  }>;
  summary: Array<{
    employeeId: string;
    employeeName: string;
    hrId: number | null;
    averageAdherence: number;
    totalScheduledMinutes: number;
    totalActualMinutes: number;
  }>;
}

/**
 * Monthly Report Response
 */
export interface MonthlyReportResponse {
  month: string;
  year: number;
  totalAgents: number;
  averageAdherence: number;
  weeklyAverages: Array<{
    weekStart: string;
    weekEnd: string;
    averageAdherence: number;
  }>;
  summary: Array<{
    employeeId: string;
    employeeName: string;
    hrId: number | null;
    averageAdherence: number;
    totalScheduledMinutes: number;
    totalActualMinutes: number;
  }>;
}

/**
 * ReportingService
 * 
 * Generates adherence reports (daily, weekly, monthly).
 * Supports JSON and CSV export formats.
 */
@Injectable()
export class ReportingService {
  private readonly logger = new Logger(ReportingService.name);

  constructor(
    @InjectRepository(AgentAdherenceSummary)
    private summaryRepo: Repository<AgentAdherenceSummary>,
    @InjectRepository(Employee)
    private employeeRepo: Repository<Employee>,
  ) {}

  /**
   * Generate daily adherence report
   */
  async generateDailyReport(
    date: string, // YYYY-MM-DD
    department?: string,
  ): Promise<DailyReportResponse> {
    this.logger.debug(`Generating daily report for ${date}, department: ${department || 'all'}`);

    // Build query for summaries on the date
    const qb = this.summaryRepo
      .createQueryBuilder('summary')
      .leftJoinAndSelect('summary.employee', 'employee')
      .where('summary.scheduleDate = :date', { date });

    // Add department join and filter if department is specified
    if (department) {
      qb.leftJoin('departments', 'department', 'department.id = employee.department_id')
        .andWhere('department.name = :department', { department });
    }

    this.logger.debug(`Executing query for daily report`);
    const summaries = await qb.getMany();
    this.logger.debug(`Found ${summaries.length} summaries for date ${date}`);

    // Calculate statistics
    const agentsWithData = summaries.length;
    const totalAdherence = summaries.reduce((sum, s) => {
      const adherence = s.adherencePercentage
        ? parseFloat(s.adherencePercentage.toString())
        : 0;
      return sum + adherence;
    }, 0);
    const averageAdherence = agentsWithData > 0 ? totalAdherence / agentsWithData : 0;

    // Calculate adherence ranges
    const ranges = {
      '90-100': 0,
      '80-89': 0,
      '70-79': 0,
      '60-69': 0,
      '0-59': 0,
    };

    summaries.forEach((summary) => {
      const adherence = summary.adherencePercentage
        ? parseFloat(summary.adherencePercentage.toString())
        : 0;
      if (adherence >= 90) ranges['90-100']++;
      else if (adherence >= 80) ranges['80-89']++;
      else if (adherence >= 70) ranges['70-79']++;
      else if (adherence >= 60) ranges['60-69']++;
      else ranges['0-59']++;
    });

    // Transform summary data
    const summaryData = summaries.map((summary) => ({
      employeeId: summary.employeeId,
      employeeName: summary.employee?.fullNameEn || 'Unknown',
      hrId: summary.employee?.hrId || null,
      adherencePercentage: summary.adherencePercentage
        ? parseFloat(summary.adherencePercentage.toString())
        : 0,
      scheduledMinutes: summary.scheduledDurationMinutes,
      actualMinutes: summary.actualDurationMinutes,
    }));

    return {
      reportDate: date,
      totalAgents: 0, // TODO: Get from employees table if needed
      agentsWithSchedule: agentsWithData, // Approximate
      agentsWithData,
      averageAdherence: Math.round(averageAdherence * 100) / 100,
      agentsByAdherenceRange: ranges,
      summary: summaryData,
    };
  }

  /**
   * Generate weekly adherence report
   */
  async generateWeeklyReport(
    weekStart: string, // YYYY-MM-DD
    weekEnd: string, // YYYY-MM-DD
    department?: string,
  ): Promise<WeeklyReportResponse> {
    this.logger.debug(`Generating weekly report from ${weekStart} to ${weekEnd}, department: ${department || 'all'}`);

    // Build query for summaries in date range
    const qb = this.summaryRepo
      .createQueryBuilder('summary')
      .leftJoinAndSelect('summary.employee', 'employee')
      .where('summary.scheduleDate >= :weekStart', { weekStart })
      .andWhere('summary.scheduleDate <= :weekEnd', { weekEnd });

    // Add department join and filter if department is specified
    if (department) {
      qb.leftJoin('departments', 'department', 'department.id = employee.department_id')
        .andWhere('department.name = :department', { department });
    }

    const summaries = await qb.getMany();

    // Group by employee and calculate averages
    const employeeMap = new Map<string, any>();

    summaries.forEach((summary) => {
      const employeeId = summary.employeeId;
      if (!employeeMap.has(employeeId)) {
        employeeMap.set(employeeId, {
          employeeId,
          employeeName: summary.employee?.fullNameEn || 'Unknown',
          hrId: summary.employee?.hrId || null,
          adherenceValues: [],
          scheduledMinutes: 0,
          actualMinutes: 0,
        });
      }

      const employeeData = employeeMap.get(employeeId);
      const adherence = summary.adherencePercentage
        ? parseFloat(summary.adherencePercentage.toString())
        : 0;
      employeeData.adherenceValues.push(adherence);
      employeeData.scheduledMinutes += summary.scheduledDurationMinutes;
      employeeData.actualMinutes += summary.actualDurationMinutes;
    });

    // Calculate averages per employee
    const summaryData = Array.from(employeeMap.values()).map((data) => ({
      employeeId: data.employeeId,
      employeeName: data.employeeName,
      hrId: data.hrId,
      averageAdherence:
        data.adherenceValues.length > 0
          ? data.adherenceValues.reduce((a: number, b: number) => a + b, 0) /
            data.adherenceValues.length
          : 0,
      totalScheduledMinutes: data.scheduledMinutes,
      totalActualMinutes: data.actualMinutes,
    }));

    // Calculate overall average
    const totalAdherence = summaryData.reduce(
      (sum, e) => sum + e.averageAdherence,
      0,
    );
    const averageAdherence =
      summaryData.length > 0 ? totalAdherence / summaryData.length : 0;

    // Calculate daily averages
    const dailyAverages: Array<{ date: string; averageAdherence: number; agentsWithData: number }> = [];
    const startDate = new Date(weekStart);
    const endDate = new Date(weekEnd);

    for (let d = new Date(startDate); d <= endDate; d.setDate(d.getDate() + 1)) {
      const dateStr = d.toISOString().split('T')[0];
      const daySummaries = summaries.filter(
        (s) => {
          const sDateStr = s.scheduleDate instanceof Date 
            ? s.scheduleDate.toISOString().split('T')[0]
            : typeof s.scheduleDate === 'string'
            ? s.scheduleDate
            : String(s.scheduleDate);
          return sDateStr === dateStr;
        },
      );
      const dayAdherence = daySummaries.reduce((sum, s) => {
        const adherence = s.adherencePercentage
          ? parseFloat(s.adherencePercentage.toString())
          : 0;
        return sum + adherence;
      }, 0);
      const dayAverage =
        daySummaries.length > 0 ? dayAdherence / daySummaries.length : 0;

      dailyAverages.push({
        date: dateStr,
        averageAdherence: Math.round(dayAverage * 100) / 100,
        agentsWithData: daySummaries.length,
      });
    }

    return {
      weekStart,
      weekEnd,
      totalAgents: employeeMap.size,
      averageAdherence: Math.round(averageAdherence * 100) / 100,
      dailyAverages,
      summary: summaryData,
    };
  }

  /**
   * Generate monthly adherence report
   */
  async generateMonthlyReport(
    month: string, // YYYY-MM
    department?: string,
  ): Promise<MonthlyReportResponse> {
    this.logger.debug(`Generating monthly report for ${month}`);

    const [year, monthNum] = month.split('-').map(Number);
    const startDate = new Date(year, monthNum - 1, 1);
    const endDate = new Date(year, monthNum, 0); // Last day of month

    const startDateStr = startDate.toISOString().split('T')[0];
    const endDateStr = endDate.toISOString().split('T')[0];

    // Build query for summaries in month
    const qb = this.summaryRepo
      .createQueryBuilder('summary')
      .leftJoinAndSelect('summary.employee', 'employee')
      .leftJoin('departments', 'department', 'department.id = employee.department_id')
      .where('summary.scheduleDate >= :startDate', { startDate: startDateStr })
      .andWhere('summary.scheduleDate <= :endDate', { endDate: endDateStr });

    if (department) {
      qb.andWhere('department.name = :department', { department });
    }

    const summaries = await qb.getMany();

    // Group by employee and calculate averages
    const employeeMap = new Map<string, any>();

    summaries.forEach((summary) => {
      const employeeId = summary.employeeId;
      if (!employeeMap.has(employeeId)) {
        employeeMap.set(employeeId, {
          employeeId,
          employeeName: summary.employee?.fullNameEn || 'Unknown',
          hrId: summary.employee?.hrId || null,
          adherenceValues: [],
          scheduledMinutes: 0,
          actualMinutes: 0,
        });
      }

      const employeeData = employeeMap.get(employeeId);
      const adherence = summary.adherencePercentage
        ? parseFloat(summary.adherencePercentage.toString())
        : 0;
      employeeData.adherenceValues.push(adherence);
      employeeData.scheduledMinutes += summary.scheduledDurationMinutes;
      employeeData.actualMinutes += summary.actualDurationMinutes;
    });

    // Calculate averages per employee
    const summaryData = Array.from(employeeMap.values()).map((data) => ({
      employeeId: data.employeeId,
      employeeName: data.employeeName,
      hrId: data.hrId,
      averageAdherence:
        data.adherenceValues.length > 0
          ? data.adherenceValues.reduce((a: number, b: number) => a + b, 0) /
            data.adherenceValues.length
          : 0,
      totalScheduledMinutes: data.scheduledMinutes,
      totalActualMinutes: data.actualMinutes,
    }));

    // Calculate overall average
    const totalAdherence = summaryData.reduce(
      (sum, e) => sum + e.averageAdherence,
      0,
    );
    const averageAdherence =
      summaryData.length > 0 ? totalAdherence / summaryData.length : 0;

    // Calculate weekly averages
    const weeklyAverages: Array<{
      weekStart: string;
      weekEnd: string;
      averageAdherence: number;
    }> = [];

    let currentWeekStart = new Date(startDate);
    while (currentWeekStart <= endDate) {
      const currentWeekEnd = new Date(currentWeekStart);
      currentWeekEnd.setDate(currentWeekEnd.getDate() + 6);
      if (currentWeekEnd > endDate) {
        currentWeekEnd.setTime(endDate.getTime());
      }

      const weekStartStr = currentWeekStart.toISOString().split('T')[0];
      const weekEndStr = currentWeekEnd.toISOString().split('T')[0];

      const weekSummaries = summaries.filter((s) => {
        const dateStr = s.scheduleDate instanceof Date 
          ? s.scheduleDate.toISOString().split('T')[0]
          : typeof s.scheduleDate === 'string'
          ? s.scheduleDate
          : String(s.scheduleDate);
        return dateStr >= weekStartStr && dateStr <= weekEndStr;
      });

      const weekAdherence = weekSummaries.reduce((sum, s) => {
        const adherence = s.adherencePercentage
          ? parseFloat(s.adherencePercentage.toString())
          : 0;
        return sum + adherence;
      }, 0);
      const weekAverage =
        weekSummaries.length > 0 ? weekAdherence / weekSummaries.length : 0;

      weeklyAverages.push({
        weekStart: weekStartStr,
        weekEnd: weekEndStr,
        averageAdherence: Math.round(weekAverage * 100) / 100,
      });

      currentWeekStart.setDate(currentWeekStart.getDate() + 7);
    }

    return {
      month,
      year,
      totalAgents: employeeMap.size,
      averageAdherence: Math.round(averageAdherence * 100) / 100,
      weeklyAverages,
      summary: summaryData,
    };
  }

  /**
   * Convert daily report to CSV format
   */
  generateDailyReportCSV(report: DailyReportResponse): string {
    const lines: string[] = [];
    
    // Header
    lines.push('Employee Name,HR ID,Department,Adherence %,Scheduled Minutes,Actual Minutes');
    
    // Data rows
    report.summary.forEach((item) => {
      lines.push(
        `"${item.employeeName}",${item.hrId || ''},${item.department || ''},${item.adherencePercentage},${item.scheduledMinutes},${item.actualMinutes}`,
      );
    });
    
    return lines.join('\n');
  }

  /**
   * Convert weekly report to CSV format
   */
  generateWeeklyReportCSV(report: WeeklyReportResponse): string {
    const lines: string[] = [];
    
    // Header
    lines.push('Employee Name,HR ID,Average Adherence %,Total Scheduled Minutes,Total Actual Minutes');
    
    // Data rows
    report.summary.forEach((item) => {
      lines.push(
        `"${item.employeeName}",${item.hrId || ''},${item.averageAdherence},${item.totalScheduledMinutes},${item.totalActualMinutes}`,
      );
    });
    
    return lines.join('\n');
  }

  /**
   * Convert monthly report to CSV format
   */
  generateMonthlyReportCSV(report: MonthlyReportResponse): string {
    const lines: string[] = [];
    
    // Header
    lines.push('Employee Name,HR ID,Average Adherence %,Total Scheduled Minutes,Total Actual Minutes');
    
    // Data rows
    report.summary.forEach((item) => {
      lines.push(
        `"${item.employeeName}",${item.hrId || ''},${item.averageAdherence},${item.totalScheduledMinutes},${item.totalActualMinutes}`,
      );
    });
    
    return lines.join('\n');
  }
}

