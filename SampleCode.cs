using Application.Common.Pagination;
using Application.DTOs.Requests.Routes;
using Application.DTOs.Responses.Amenities;
using Application.DTOs.Responses.Routes;
using Application.DTOs.Responses.Stations;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Configuration.Data;
using ExcelDataReader;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Infrastructure.Repositories
{
    public class RouteRepository(AppDbContext context, IPricingService pricingService) : IRouteRepository
    {
        private readonly AppDbContext _context = context;
        private readonly IPricingService _pricingService = pricingService;

        public async Task<PaginatedResponse<SearchRouteResponse>> SearchRoutesAsync(SearchRoutesRequest request, CancellationToken cancellationToken)
        {
            var query = _context.Routes
                .Include(r => r.Operator)
                .Include(r => r.Schedules)
                    .ThenInclude(s => s.ExceptionDays)
                .Include(r => r.Schedules)
                    .ThenInclude(s => s.ScheduleTimes)
                        .ThenInclude(st => st.ScheduleStations)
                            .ThenInclude(ss => ss.Station)
                .Where(r => r.Origin == request.Origin && r.Destination == request.Destination);

            var date = request.Date.Date;
            var dayOfWeek = date.DayOfWeek;
            bool isSaturday = dayOfWeek == DayOfWeek.Saturday;
            bool isSunday = dayOfWeek == DayOfWeek.Sunday;
            bool isWeekday = !isSaturday && !isSunday;

            decimal discountFactor = _pricingService.GetDiscountFactor(request.PassengerType);

            if (request.MinPrice.HasValue)
                query = query.Where(r => (r.Price * discountFactor) >= request.MinPrice.Value);
            if (request.MaxPrice.HasValue)
                query = query.Where(r => (r.Price * discountFactor) <= request.MaxPrice.Value);

            if (request.StationIds.Any())
            {
                query = query.Where(r => r.Schedules.Any(s =>
                    s.ScheduleTimes.Any(st =>
                        st.ScheduleStations.Any(ss => request.StationIds.Contains(ss.StationId))
                    )
                ));
            }

            query = query.Where(r => r.Schedules.Any(s =>
                s.ValidFrom <= date &&
                s.ValidTo >= date &&
                !s.ExceptionDays.Any(e => e.Date == date)
            ));

            var currentTime = DateTime.Now;
            if (request.Date.Date == currentTime.Date)
            {
                query = query.Where(r => r.Schedules.Any(s =>
                    s.ScheduleTimes.Any(st => st.DepartureTime > currentTime.TimeOfDay)
                ));
            }

            if (request.OperatorNames.Any())
                query = query.Where(r => request.OperatorNames.Contains(r.Operator!.Name ?? ""));

            if (request.HasWiFi.HasValue)
                query = query.Where(r => r.Amenity.HasWiFi == request.HasWiFi.Value);

            if (request.HasAC.HasValue)
                query = query.Where(r => r.Amenity.HasAirConditioning == request.HasAC.Value);

            var scheduleTimesQuery = query
                .SelectMany(r => r.Schedules
                    .SelectMany(s => s.ScheduleTimes
                        .Where(st =>
                            s.ValidFrom <= date &&
                            s.ValidTo >= date &&
                            (!s.ExceptionDays.Any(e => e.Date == date))
                        )
                        .Select(st => new
                        {
                            RouteId = r.Id,
                            r.Origin,
                            r.Destination,
                            Price = r.Price * discountFactor,
                            ReturnTicketPrice = r.ReturnTicketPrice * discountFactor,
                            OperatorName = r.Operator!.Name,
                            
                            Amenity = new AmenityResponse
                            {
                                NumberOfSeats = r.Amenity.NumberOfSeats,
                                LuggageCapacity = r.Amenity.LuggageCapacity,
                                HasWiFi = r.Amenity.HasWiFi,
                                HasAirConditioning = r.Amenity.HasAirConditioning,
                                HasPowerOutlets = r.Amenity.HasPowerOutlets,
                                HasRestroom = r.Amenity.HasRestroom
                            },

                            ScheduleId = s.Id,
                            ScheduleTimeId = st.Id,
                            s.ValidFrom,
                            s.ValidTo,
                            st.DepartureTime,
                            st.ArrivalTime,
                            st.IsWeekdayActive,
                            st.IsSaturdayActive,
                            st.IsSundayActive,
                            st.IsHolidayActive,
                            ScheduleStations = st.ScheduleStations
                                .OrderBy(ss => ss.ArrivalTime)
                                .Select(ss => new ScheduleStationResponse
                                {
                                    ScheduleStationId = ss.Id,
                                    StationId = ss.StationId,
                                    Name = ss.Station.Name,
                                    ArrivalTime = ss.ArrivalTime.ToString(@"hh\:mm"),
                                    Distance = ss.DistanceFromPreviousStop
                                }).ToList()
                        })
                    )
                );

            var scheduleTimes = await scheduleTimesQuery.ToListAsync(cancellationToken);

            scheduleTimes = request.SortBy switch
            {
                SortByOptions.Price => request.IsAscending
                    ? scheduleTimes.OrderBy(r => r.Price).ToList()
                    : scheduleTimes.OrderByDescending(r => r.Price).ToList(),
                SortByOptions.Duration => request.IsAscending
                    ? scheduleTimes.OrderBy(r => (r.ArrivalTime - r.DepartureTime)).ToList()
                    : scheduleTimes.OrderByDescending(r => (r.ArrivalTime - r.DepartureTime)).ToList(),
                _ => request.IsAscending
                    ? scheduleTimes.OrderBy(r => r.DepartureTime).ToList()
                    : scheduleTimes.OrderByDescending(r => r.DepartureTime).ToList(),
            };

            var paginatedData = scheduleTimes
                .Skip((request.PageNumber - 1) * request.PageSize)
                .Take(request.PageSize)
                .Select(st => new SearchRouteResponse
                {
                    RouteId = st.RouteId,
                    Origin = st.Origin,
                    Destination = st.Destination,
                    Price = st.Price,
                    ReturnTicketPrice = st.ReturnTicketPrice,
                    PassengerType = request.PassengerType,
                    OperatorName = st.OperatorName,
                    Amenity = st.Amenity,
                    ScheduleId = st.ScheduleId,
                    ScheduleTimeId = st.ScheduleTimeId,
                    ValidFrom = st.ValidFrom,
                    ValidTo = st.ValidTo,
                    DepartureTime = st.DepartureTime.ToString(@"hh\:mm"),
                    ArrivalTime = st.ArrivalTime.ToString(@"hh\:mm"),
                    Duration = GetFormattedDuration(st.DepartureTime, st.ArrivalTime),
                    IsWeekdayActive = st.IsWeekdayActive,
                    IsSaturdayActive = st.IsSaturdayActive,
                    IsSundayActive = st.IsSundayActive,
                    IsHolidayActive = st.IsHolidayActive,
                    ScheduleStations = st.ScheduleStations
                })
                .ToList();

            var totalRecords = scheduleTimes.Count;

            return new PaginatedResponse<SearchRouteResponse>(paginatedData, request.PageNumber, request.PageSize, totalRecords);
        }
    }
}
