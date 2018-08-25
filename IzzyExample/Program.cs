using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Automatonymous;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Exceptions;
using EventFlow.Extensions;
using EventFlow.Queries;
using EventFlow.ReadStores;
using EventFlow.ValueObjects;
using Newtonsoft.Json;
using static System.Console;
using static System.Guid;

namespace ConsoleApp1
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            await new App().Run(CancellationToken.None);
            return 0;
        }
    }

    class App
    {
        public async Task Run(CancellationToken cancelToken)
        {
            using (var resolver = EventFlowOptions.New
                .AddEvents(typeof(NewPaymentStarted))
                .AddCommands(typeof(StartNewPaymentCommand))
                .AddCommandHandlers(typeof(StartNewPaymentCommandHandler))
                .UseInMemoryReadStoreFor<PaymentGroupRM>()
                .CreateResolver())
            {
                var commandBus = resolver.Resolve<ICommandBus>();
                var queryProcessor = resolver.Resolve<IQueryProcessor>();

                var paymentGroupId = PaymentGroupId.New;
                CorrelationId corrId = NewGuid().ToString();

                foreach (var _ in Enumerable.Range(1, count: 2))
                {
                    var payment = new Payment
                    {
                        Id = NewGuid().ToString(),
                        Country = "PL",
                        Money = "42.5 PLN"
                    };

                    var result = await commandBus.PublishAsync(new StartNewPaymentCommand(paymentGroupId, payment, corrId), cancelToken).ConfigureAwait(false);
                    WriteLine($"Result success: {result.IsSuccess}");

                    var rm = await queryProcessor.ProcessAsync(new PaymentGroupByIdQuery<PaymentGroupRM>((string)paymentGroupId), cancelToken);
                }

                //Console.WriteLine(exampleReadModel.MagicNumber);
                WriteLine("==== Done ====");
            }
        }

        public class PaymentGroupAggregate : AggregateRoot<PaymentGroupAggregate, PaymentGroupId>, IEmit<NewPaymentStarted>
        {
            public CorrelationId CorrelationId { get; private set; }
            protected List<Payment> Payments { get; } // TODO Consider changing it to own type of collection, f.e. adding some logic
            public IReadOnlyCollection<Payment> PaymentsReadOnly => Payments.AsReadOnly();
            public bool Ready { get; set; }

            public PaymentGroupAggregate(PaymentGroupId id) : base(id)
            {
                Payments = new List<Payment>();
                Ready = true;
            }

            public void StartNewPayment(Payment payment, CorrelationId correlationId)
            {
                if (!Ready) // Part of validation can happen in State machine
                    throw DomainError.With("Cannot start new payment while payment group is already processing one.");

                if (Payments.Any(p => p.Id == payment.Id))
                    throw DomainError.With("The payment with such ID already exists in the payment group.");

                // TODO Run Payment through State Machine



                Emit(new NewPaymentStarted(payment, correlationId));
            }

            public void Apply(NewPaymentStarted aggregateEvent)
            {
                Payments.Add(aggregateEvent.Payment);
                Ready = false;
            }

            private bool IsFirstPaymentInGroup() // This can be turned into specification
            {
                return CorrelationId == null && Payments.Count == 0;
            }
        }

        public interface IApplicableByStateMachine
        {
            int State { get; }
        }

        [Serializable]
        // The payment will be serialized as the part of event in event store.
        // I will NOT use parametrized ctros to not deal with serializers (for now).
        // We should also check if all contained classes are serializable.
        public class Payment : IApplicableByStateMachine
        {
            public PaymentId Id { get; set; }

            public string Money { get; set; }
            public string Country { get; set; }

            public StatusEnum Status { get; set; }

            public int State
            {
                get => (int)Status;
                set => Status = (StatusEnum)value;
            }

            // [AutomatonymousStateMachineStates]
            public enum StatusEnum
            {
                Initialized = 3,
                Ready = 4,
                Started = 5,
                Charged = 6,
                Failed = 7,
                Suspended = 8,
                Refunded = 9,
                Settled = 10
            }
        }

        public class PaymentGroupId : EventFlow.Core.Identity<PaymentGroupId>
        {
            public PaymentGroupId(string value) : base(value) { }

            public static implicit operator PaymentGroupId(string value) => new PaymentGroupId(value);
            public static implicit operator string(PaymentGroupId id) => id.Value;
        }

        [JsonConverter(typeof(SingleValueObjectConverter))]
        public class CorrelationId : SingleValueObject<string>
        {
            public CorrelationId(string value) : base(value)
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));
            }

            public static implicit operator CorrelationId(string value) => new CorrelationId(value);
            public static implicit operator string(CorrelationId id) => id.Value;
        }

        [JsonConverter(typeof(SingleValueObjectConverter))]
        public class PaymentId : SingleValueObject<string>
        {
            public PaymentId(string value) : base(value)
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));
            }

            public static implicit operator PaymentId(string value) => new PaymentId(value);
            public static implicit operator string(PaymentId id) => id.Value;
        }

        public class NewPaymentStarted : AggregateEvent<PaymentGroupAggregate, PaymentGroupId>
        {
            public Payment Payment { get; }
            public CorrelationId CorrelationId { get; }

            public NewPaymentStarted(Payment payment, CorrelationId correlationId)
            {
                Payment = payment;
                CorrelationId = correlationId;
            }
        }

        public class StartNewPaymentCommand : Command<PaymentGroupAggregate, PaymentGroupId>
        {
            public Payment Payment { get; }
            public CorrelationId CorrelationId { get; }

            public StartNewPaymentCommand(PaymentGroupId aggregateId, Payment payment, CorrelationId correlationId) : base(aggregateId)
            {
                Payment = payment;
                CorrelationId = correlationId;
            }
        }

        // Command handler for our command
        public class StartNewPaymentCommandHandler : CommandHandler<PaymentGroupAggregate, PaymentGroupId, IExecutionResult, StartNewPaymentCommand>
        {
            public override Task<IExecutionResult> ExecuteCommandAsync(PaymentGroupAggregate aggregate, StartNewPaymentCommand command, CancellationToken cancellationToken)
            {
                try
                {
                    aggregate.StartNewPayment(command.Payment, command.CorrelationId);

                }
                catch (DomainError e)
                {
                    return Task.FromResult<IExecutionResult>(new FailedExecutionResult(new[] { e.Message }));
                }

                return Task.FromResult<IExecutionResult>(new SuccessExecutionResult());

            }
        }

        public class PaymentGroupRM : IReadModel, IAmReadModelFor<PaymentGroupAggregate, PaymentGroupId, NewPaymentStarted>
        {
            public void Apply(
              IReadModelContext context,
              IDomainEvent<PaymentGroupAggregate, PaymentGroupId, NewPaymentStarted> domainEvent)
            {
                // TODO
            }
        }

        public class PaymentStateMachine : AutomatonymousStateMachine<Payment>
        {
            public PaymentStateMachine()
            {
                InstanceState(
                    instances payment => (payment as IApplicableByStateMachine).State,
                    states: new[] { Initialized });
            }


            public State Initialized { get; private set; }

        }

    }
}
