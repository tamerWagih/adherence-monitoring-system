import {
  Entity,
  PrimaryGeneratedColumn,
  Column,
  Index,
} from 'typeorm';

/**
 * Agent Schedule Entity (Minimal)
 * 
 * Used for querying break schedules from agent_schedules table.
 * Only includes fields needed for break detection.
 * 
 * Note: This is a minimal copy for the adherence backend.
 * The full entity exists in people-ops-system, but we use this
 * to avoid cross-repo dependencies while sharing the same database.
 */
@Entity('agent_schedules')
@Index('idx_agent_schedules_employee_date', ['employeeId', 'scheduleDate'])
@Index('idx_agent_schedules_date', ['scheduleDate'])
export class AgentSchedule {
  @PrimaryGeneratedColumn('uuid')
  id: string;

  @Column({ name: 'employee_id', type: 'uuid' })
  employeeId: string;

  @Column({ name: 'schedule_date', type: 'date' })
  scheduleDate: Date;

  @Column({ name: 'shift_start', type: 'time' })
  shiftStart: string; // Format: "HH:mm:ss" or "HH:mm"

  @Column({ name: 'shift_end', type: 'time' })
  shiftEnd: string; // Format: "HH:mm:ss" or "HH:mm"

  @Column({ name: 'is_break', type: 'boolean', default: false })
  isBreak: boolean;

  @Column({ name: 'break_duration', type: 'int', default: 0 })
  breakDuration: number; // in minutes

  @Column({ name: 'is_confirmed', type: 'boolean', default: true })
  isConfirmed: boolean;
}
