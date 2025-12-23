import {
  IsUUID,
  IsDateString,
  IsString,
  IsNumber,
  IsInt,
  IsOptional,
  Min,
  Max,
} from 'class-validator';
import { Type as TransformType } from 'class-transformer';
import { ApiPropertyOptional } from '@nestjs/swagger';

export class AdherenceQueryDto {
  @ApiPropertyOptional({
    description: 'Filter by employee UUID',
    example: '04eb6c2e-ecbe-4bf5-a182-64ddf6a05aa6',
  })
  @IsUUID()
  @IsOptional()
  employee_id?: string;

  @ApiPropertyOptional({
    description: 'Start date in YYYY-MM-DD format',
    example: '2025-12-22',
  })
  @IsDateString()
  @IsOptional()
  start_date?: string;

  @ApiPropertyOptional({
    description: 'End date in YYYY-MM-DD format',
    example: '2025-12-22',
  })
  @IsDateString()
  @IsOptional()
  end_date?: string;

  @ApiPropertyOptional({
    description: 'Filter by department name',
    example: 'IT',
  })
  @IsString()
  @IsOptional()
  department?: string;

  @ApiPropertyOptional({
    description: 'Minimum adherence percentage (0-100)',
    example: 80,
    minimum: 0,
    maximum: 100,
  })
  @IsNumber()
  @Min(0)
  @Max(100)
  @IsOptional()
  @TransformType(() => Number)
  min_adherence?: number;

  @ApiPropertyOptional({
    description: 'Maximum adherence percentage (0-100)',
    example: 100,
    minimum: 0,
    maximum: 100,
  })
  @IsNumber()
  @Min(0)
  @Max(100)
  @IsOptional()
  @TransformType(() => Number)
  max_adherence?: number;

  @ApiPropertyOptional({
    description: 'Page number (default: 1)',
    example: 1,
    minimum: 1,
    default: 1,
  })
  @IsInt()
  @Min(1)
  @IsOptional()
  @TransformType(() => Number)
  page?: number = 1;

  @ApiPropertyOptional({
    description: 'Items per page (default: 50, max: 100)',
    example: 50,
    minimum: 1,
    maximum: 100,
    default: 50,
  })
  @IsInt()
  @Min(1)
  @Max(100)
  @IsOptional()
  @TransformType(() => Number)
  limit?: number = 50;
}

